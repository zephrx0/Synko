import { button, iconButton } from "./components/buttons.js";
import { toggle, sourceChip, segmentedGroup } from "./components/selection.js";
import { textField } from "./components/fields.js";
import { showDialog, showSnackbar } from "./components/overlays.js";

export function mountGallery(root) {
  root.className = "component-gallery";
  const row = document.createElement("div"); row.className = "component-row";
  row.append(button("Далее", { variant: "filled", action: () => showSnackbar("Не удалось выполнить действие") }), iconButton("settings", "Настройки", () => showDialog("Название трека, исполнитель и альбом отправляются в каталог Apple, чтобы найти обложку.")), toggle("Показывать в Discord", true, enabled => { root.dataset.enabled = String(enabled); }));
  const field = textField("Application ID"); field.input.inputMode = "numeric";
  const source = segmentedGroup("Источник", [{ label: "Авто", value: "auto" }, { label: "Apple Music", value: "apple" }, { label: "Браузер", value: "browser" }], "auto", value => { root.dataset.source = value; });
  const chips = document.createElement("div"); chips.className = "component-row";
  chips.append(sourceChip("Apple Music · играет", "music_note", true, () => showDialog("Название трека, исполнитель и альбом отправляются в каталог Apple, чтобы найти обложку.")), sourceChip("Chrome · пауза", "language", false, () => showDialog("Ссылка на ролик берётся из адресной строки YouTube и передаётся в Discord как превью. История браузера не читается.")));
  root.append(row, field, source, chips);
}
