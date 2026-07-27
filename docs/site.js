const languageButton = document.querySelector(".language");
const languageNodes = document.querySelectorAll("[data-en][data-ru]");
let language = localStorage.getItem("vexark-language") || "en";

function applyLanguage() {
  document.documentElement.lang = language;
  languageNodes.forEach((node) => {
    node.innerHTML = node.dataset[language];
  });
  languageButton.textContent = language === "en" ? "RU" : "EN";
  document.title = language === "en"
    ? "VeXArk — Your Android data, yours"
    : "VeXArk — ваши данные остаются вашими";
}

languageButton.addEventListener("click", () => {
  language = language === "en" ? "ru" : "en";
  localStorage.setItem("vexark-language", language);
  applyLanguage();
});

const preview = document.querySelector(".screen-frame img");
document.querySelectorAll(".screen-tabs button").forEach((button) => {
  button.addEventListener("click", () => {
    document.querySelector(".screen-tabs button.active")?.classList.remove("active");
    button.classList.add("active");
    preview.style.opacity = "0";
    window.setTimeout(() => {
      preview.src = button.dataset.image;
      preview.alt = `VeXArk ${button.textContent.trim()} theme preview`;
      preview.style.opacity = "1";
    }, 140);
  });
});

applyLanguage();

