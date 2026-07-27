using PhoneBackup.Core;

namespace PhoneBackup.Core.Tests;

public sealed class SafetyPolicyTests
{
    [Theory]
    [InlineData("../data")]
    [InlineData("safe/../../escape")]
    [InlineData("/absolute/path")]
    [InlineData("C:\\absolute")]
    public void RejectsUnsafeRestorePaths(string path) =>
        Assert.False(RestorePathPolicy.IsSafeRelativePath(path));

    [Theory]
    [InlineData("files/database.sqlite")]
    [InlineData("shared_prefs/settings.xml")]
    [InlineData("sub dir/file.txt")]
    public void AcceptsSafeRestorePaths(string path) =>
        Assert.True(RestorePathPolicy.IsSafeRelativePath(path));

    [Theory]
    [InlineData("DCIM/Camera/photo.jpg")]
    [InlineData("Download/telegram/video.MP4")]
    [InlineData("Pictures/image.heic")]
    public void SharedMediaIsExcluded(string path) =>
        Assert.True(new MediaExclusionPolicy().ShouldExcludeSharedPath(path));

    [Theory]
    [InlineData("Download/app.apk")]
    [InlineData("Music/song.flac")]
    [InlineData("Documents/report.pdf")]
    public void DocumentsAndAudioAreIncluded(string path) =>
        Assert.False(new MediaExclusionPolicy().ShouldExcludeSharedPath(path));
}

