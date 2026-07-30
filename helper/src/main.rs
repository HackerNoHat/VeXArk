use base64::Engine;
use clap::{Parser, Subcommand};
use serde::Serialize;
use std::ffi::CString;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt, PermissionsExt};
use std::path::{Component, Path, PathBuf};
use walkdir::WalkDir;

const ALLOWED_ROOTS: &[&str] = &[
    "/data/user/",
    "/data/user_de/",
    "/storage/emulated/",
    "/data/adb/modules/",
    "/data/misc/apexdata/com.android.wifi/",
];

#[derive(Parser)]
#[command(version, about = "Restricted root filesystem helper for PhoneBackup")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    Probe,
    Scan {
        #[arg(long)]
        root: PathBuf,
        #[arg(long, default_value_t = false)]
        include_caches: bool,
        #[arg(long, default_value_t = false)]
        full_hash: bool,
    },
    Read {
        #[arg(long)]
        root: PathBuf,
        #[arg(long)]
        relative: PathBuf,
    },
    ReadBase64 {
        #[arg(long)]
        root: PathBuf,
        #[arg(long)]
        relative: PathBuf,
    },
    Restore {
        #[arg(long)]
        root: PathBuf,
        #[arg(long)]
        relative: PathBuf,
        #[arg(long)]
        mode: u32,
        #[arg(long)]
        uid: u32,
        #[arg(long)]
        gid: u32,
        #[arg(long)]
        modified_unix_nanos: i64,
        #[arg(long)]
        selinux_label: Option<String>,
    },
    RestoreDirectory {
        #[arg(long)]
        root: PathBuf,
        #[arg(long)]
        relative: PathBuf,
        #[arg(long)]
        mode: u32,
        #[arg(long)]
        uid: u32,
        #[arg(long)]
        gid: u32,
        #[arg(long)]
        modified_unix_nanos: i64,
        #[arg(long)]
        selinux_label: Option<String>,
    },
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Probe {
    protocol_version: u32,
    helper_version: &'static str,
    uid: u32,
    allowed_roots: &'static [&'static str],
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FileEntry {
    relative_path: String,
    kind: &'static str,
    size: u64,
    modified_unix_nanos: i128,
    mode: u32,
    uid: u32,
    gid: u32,
    link_target: Option<String>,
    selinux_label: Option<String>,
    content_hash: Option<String>,
}

fn main() {
    if let Err(error) = run(Cli::parse()) {
        let _ = writeln!(io::stderr().lock(), "{error}");
        std::process::exit(2);
    }
}

fn run(cli: Cli) -> Result<(), Box<dyn std::error::Error>> {
    match cli.command {
        Command::Probe => {
            println!(
                "{}",
                serde_json::to_string(&Probe {
                    protocol_version: 1,
                    helper_version: env!("CARGO_PKG_VERSION"),
                    uid: unsafe { getuid() },
                    allowed_roots: ALLOWED_ROOTS,
                })?
            );
        }
        Command::Scan {
            root,
            include_caches,
            full_hash,
        } => scan(&validated_root(root)?, include_caches, full_hash)?,
        Command::Read { root, relative } => {
            let root = validated_root(root)?;
            let path = safe_existing_path(&root, &relative)?;
            let metadata = fs::symlink_metadata(&path)?;
            if !metadata.is_file() {
                return Err("read target is not a regular file".into());
            }
            io::copy(&mut File::open(path)?, &mut io::stdout().lock())?;
        }
        Command::ReadBase64 { root, relative } => {
            let root = validated_root(root)?;
            let path = safe_existing_path(&root, &relative)?;
            let metadata = fs::symlink_metadata(&path)?;
            if !metadata.is_file() {
                return Err("read target is not a regular file".into());
            }
            let mut input = File::open(path)?;
            let stdout = io::stdout();
            let mut output = stdout.lock();
            let mut buffer = [0_u8; 48 * 1024];
            loop {
                let count = input.read(&mut buffer)?;
                if count == 0 {
                    break;
                }
                let encoded = base64::engine::general_purpose::STANDARD.encode(&buffer[..count]);
                output.write_all(encoded.as_bytes())?;
                output.write_all(b"\n")?;
            }
        }
        Command::Restore {
            root,
            relative,
            mode,
            uid,
            gid,
            modified_unix_nanos,
            selinux_label,
        } => restore_file(
            &validated_root(root)?,
            &relative,
            mode,
            uid,
            gid,
            modified_unix_nanos,
            selinux_label.as_deref(),
        )?,
        Command::RestoreDirectory {
            root,
            relative,
            mode,
            uid,
            gid,
            modified_unix_nanos,
            selinux_label,
        } => restore_directory(
            &validated_root(root)?,
            &relative,
            mode,
            uid,
            gid,
            modified_unix_nanos,
            selinux_label.as_deref(),
        )?,
    }
    Ok(())
}

fn scan(root: &Path, include_caches: bool, full_hash: bool) -> Result<(), Box<dyn std::error::Error>> {
    let stdout = io::stdout();
    let mut output = stdout.lock();
    let walker = WalkDir::new(root)
        .follow_links(false)
        .sort_by_file_name()
        .into_iter()
        .filter_entry(|entry| {
            include_caches
                || !entry.file_type().is_dir()
                || !matches!(
                    entry.file_name().to_string_lossy().as_ref(),
                    "cache" | "code_cache"
                )
        });
    for item in walker {
        let item = item?;
        let path = item.path();
        let relative = path.strip_prefix(root)?;
        if relative.as_os_str().is_empty() {
            continue;
        }
        let metadata = fs::symlink_metadata(path)?;
        let kind = if metadata.is_dir() {
            "directory"
        } else if metadata.is_file() {
            "file"
        } else if metadata.file_type().is_symlink() {
            "symlink"
        } else {
            "special"
        };
        let name = relative.to_string_lossy().replace('\\', "/");
        let critical = name.ends_with(".db")
            || name.ends_with(".sqlite")
            || name.ends_with(".xml")
            || name.contains("shared_prefs/");
        let content_hash = if metadata.is_file() && (full_hash || critical || metadata.len() <= 1024 * 1024) {
            Some(hash_file(path)?)
        } else {
            None
        };
        let modified_unix_nanos =
            (metadata.mtime() as i128) * 1_000_000_000 + metadata.mtime_nsec() as i128;
        let entry = FileEntry {
            relative_path: name,
            kind,
            size: metadata.len(),
            modified_unix_nanos,
            mode: metadata.mode(),
            uid: metadata.uid(),
            gid: metadata.gid(),
            link_target: if metadata.file_type().is_symlink() {
                Some(fs::read_link(path)?.to_string_lossy().into_owned())
            } else {
                None
            },
            selinux_label: read_selinux_label(path),
            content_hash,
        };
        serde_json::to_writer(&mut output, &entry)?;
        output.write_all(b"\n")?;
    }
    Ok(())
}

fn restore_file(
    root: &Path,
    relative: &Path,
    mode: u32,
    uid: u32,
    gid: u32,
    modified_unix_nanos: i64,
    selinux_label: Option<&str>,
) -> Result<(), Box<dyn std::error::Error>> {
    validate_relative(relative)?;
    let target = root.join(relative);
    let parent = target.parent().ok_or("restore target has no parent")?;
    ensure_no_symlink_components(root, parent)?;
    fs::create_dir_all(parent)?;
    ensure_no_symlink_components(root, parent)?;

    let temporary = target.with_extension(format!("phonebackup-{}.tmp", std::process::id()));
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .mode(0o600)
        .open(&temporary)?;
    io::copy(&mut io::stdin().lock(), &mut file)?;
    file.sync_all()?;
    fs::set_permissions(&temporary, fs::Permissions::from_mode(mode & 0o7777))?;
    set_owner(&temporary, uid, gid)?;
    set_modified_time(&temporary, modified_unix_nanos)?;
    if let Some(label) = selinux_label {
        set_selinux_label(&temporary, label)?;
    }
    fs::rename(temporary, target)?;
    Ok(())
}

fn restore_directory(
    root: &Path,
    relative: &Path,
    mode: u32,
    uid: u32,
    gid: u32,
    modified_unix_nanos: i64,
    selinux_label: Option<&str>,
) -> Result<(), Box<dyn std::error::Error>> {
    validate_relative(relative)?;
    let target = root.join(relative);
    let parent = target.parent().ok_or("restore target has no parent")?;
    ensure_no_symlink_components(root, parent)?;
    fs::create_dir_all(&target)?;
    ensure_no_symlink_components(root, &target)?;
    fs::set_permissions(&target, fs::Permissions::from_mode(mode & 0o7777))?;
    set_owner(&target, uid, gid)?;
    set_modified_time(&target, modified_unix_nanos)?;
    if let Some(label) = selinux_label {
        set_selinux_label(&target, label)?;
    }
    Ok(())
}

fn read_selinux_label(path: &Path) -> Option<String> {
    let path = CString::new(path.as_os_str().as_encoded_bytes()).ok()?;
    let name = c"security.selinux";
    let size = unsafe { libc::getxattr(path.as_ptr(), name.as_ptr(), std::ptr::null_mut(), 0) };
    if size <= 0 {
        return None;
    }
    let mut value = vec![0_u8; size as usize];
    let actual = unsafe {
        libc::getxattr(
            path.as_ptr(),
            name.as_ptr(),
            value.as_mut_ptr().cast(),
            value.len(),
        )
    };
    if actual <= 0 {
        return None;
    }
    value.truncate(actual as usize);
    if value.last() == Some(&0) {
        value.pop();
    }
    String::from_utf8(value).ok()
}

fn set_owner(path: &Path, uid: u32, gid: u32) -> Result<(), Box<dyn std::error::Error>> {
    let path = CString::new(path.as_os_str().as_encoded_bytes())?;
    if unsafe { libc::chown(path.as_ptr(), uid, gid) } != 0 {
        return Err(io::Error::last_os_error().into());
    }
    Ok(())
}

fn set_modified_time(path: &Path, unix_nanos: i64) -> Result<(), Box<dyn std::error::Error>> {
    let path = CString::new(path.as_os_str().as_encoded_bytes())?;
    let seconds = unix_nanos.div_euclid(1_000_000_000);
    let nanos = unix_nanos.rem_euclid(1_000_000_000);
    let times = [
        libc::timespec { tv_sec: seconds, tv_nsec: nanos },
        libc::timespec { tv_sec: seconds, tv_nsec: nanos },
    ];
    if unsafe { libc::utimensat(libc::AT_FDCWD, path.as_ptr(), times.as_ptr(), 0) } != 0 {
        return Err(io::Error::last_os_error().into());
    }
    Ok(())
}

fn set_selinux_label(path: &Path, label: &str) -> Result<(), Box<dyn std::error::Error>> {
    let path = CString::new(path.as_os_str().as_encoded_bytes())?;
    let label = CString::new(label)?;
    let name = c"security.selinux";
    if unsafe {
        libc::setxattr(
            path.as_ptr(),
            name.as_ptr(),
            label.as_ptr().cast(),
            label.as_bytes().len() + 1,
            0,
        )
    } != 0
    {
        return Err(io::Error::last_os_error().into());
    }
    Ok(())
}

fn validated_root(root: PathBuf) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let canonical = root.canonicalize()?;
    let value = format!("{}/", canonical.to_string_lossy().trim_end_matches('/'));
    if !ALLOWED_ROOTS.iter().any(|prefix| value.starts_with(prefix)) {
        return Err(format!("root is not allow-listed: {value}").into());
    }
    Ok(canonical)
}

fn validate_relative(relative: &Path) -> Result<(), Box<dyn std::error::Error>> {
    if relative.as_os_str().is_empty() || relative.is_absolute() {
        return Err("relative path is empty or absolute".into());
    }
    for component in relative.components() {
        if !matches!(component, Component::Normal(_)) {
            return Err("relative path contains traversal".into());
        }
    }
    Ok(())
}

fn safe_existing_path(root: &Path, relative: &Path) -> Result<PathBuf, Box<dyn std::error::Error>> {
    validate_relative(relative)?;
    let candidate = root.join(relative).canonicalize()?;
    if !candidate.starts_with(root) {
        return Err("path escapes root".into());
    }
    Ok(candidate)
}

fn ensure_no_symlink_components(root: &Path, parent: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let relative = parent.strip_prefix(root)?;
    let mut current = root.to_path_buf();
    for component in relative.components() {
        current.push(component);
        if let Ok(metadata) = fs::symlink_metadata(&current) {
            if metadata.file_type().is_symlink() {
                return Err(format!("symlink component rejected: {}", current.display()).into());
            }
        }
    }
    Ok(())
}

fn hash_file(path: &Path) -> Result<String, Box<dyn std::error::Error>> {
    let mut file = File::open(path)?;
    let mut hasher = blake3::Hasher::new();
    let mut buffer = [0_u8; 256 * 1024];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hasher.finalize().to_hex().to_string())
}

unsafe extern "C" {
    fn getuid() -> u32;
}
