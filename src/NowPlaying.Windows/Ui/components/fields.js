let sequence = 0;
export function textField(label, value = "", changed = null) {
  const field = document.createElement("div");
  field.className = "text-field";
  const caption = document.createElement("label"), input = document.createElement("input");
  input.id = "field-" + ++sequence;
  input.value = value;
  input.autocomplete = "off";
  input.spellcheck = false;
  caption.htmlFor = input.id;
  caption.textContent = label;
  if (changed) input.addEventListener("input", () => changed(input.value));
  field.append(caption, input);
  field.input = input;
  field.setInvalid = invalid => { field.setAttribute("aria-invalid", String(invalid)); input.setAttribute("aria-invalid", String(invalid)); };
  return field;
}
