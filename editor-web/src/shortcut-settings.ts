/** Use explicit selectors: an already registered global key cannot be captured reliably by a web input. */
export function shortcutSettings(
  current: string,
  save: (value: string) => Promise<void>,
): HTMLElement {
  const section = document.createElement("div");
  section.className = "card";
  section.id = "quick-shortcut-settings";
  const heading = document.createElement("h2");
  heading.textContent = "クイックメモのショートカット";
  const saved = document.createElement("p");
  saved.textContent = `現在の設定: ${current || "無効"}`;
  saved.id = "quick-shortcut-current";
  const hint = document.createElement("p");
  hint.textContent =
    "他のアプリを操作中でも呼び出せます。CtrlまたはAltと、英字・数字・ファンクションキーを選択してください。";
  const form = document.createElement("fieldset");
  form.style.border = "0";
  const enabled = checkbox("ショートカットを使用する", current !== "");
  const keys = document.createElement("div");
  keys.style.cssText =
    "display:flex;gap:16px;align-items:center;flex-wrap:wrap";
  const ctrl = checkbox("Ctrl", false),
    alt = checkbox("Alt", false),
    shift = checkbox("Shift", false);
  const label = document.createElement("label");
  label.textContent = "キー ";
  const key = document.createElement("select");
  key.setAttribute("aria-label", "クイックメモのキー");
  for (const value of [
    ..."ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
    ...Array.from({ length: 24 }, (_, i) => `F${i + 1}`).filter(
      (v) => v !== "F12",
    ),
  ]) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value;
    key.append(option);
  }
  label.append(key);
  keys.append(ctrl.label, alt.label, shift.label, label);
  const feedback = document.createElement("p");
  feedback.setAttribute("role", "status");
  feedback.id = "quick-shortcut-feedback";
  const apply = document.createElement("button");
  apply.textContent = "ショートカットを保存";
  const reset = document.createElement("button");
  reset.textContent = "初期値に戻す";
  reset.style.marginLeft = "8px";
  function refreshEnabled() {
    for (const input of [ctrl.input, alt.input, shift.input, key])
      input.disabled = !enabled.input.checked;
  }
  function load(value: string) {
    enabled.input.checked = value !== "";
    const parts = (value || "Ctrl+Alt+M").split("+");
    ctrl.input.checked = parts.includes("Ctrl");
    alt.input.checked = parts.includes("Alt");
    shift.input.checked = parts.includes("Shift");
    key.value = parts.at(-1)!;
    refreshEnabled();
  }
  enabled.input.onchange = refreshEnabled;
  reset.onclick = () => {
    load("Ctrl+Alt+M");
    feedback.textContent =
      "初期値を選択しました。「ショートカットを保存」で適用します。";
  };
  apply.onclick = async () => {
    const modifiers = [ctrl, alt, shift]
      .filter((c) => c.input.checked)
      .map((c) => c.input.value);
    if (enabled.input.checked && !ctrl.input.checked && !alt.input.checked) {
      feedback.textContent = "CtrlまたはAltを含めてください。";
      return;
    }
    const value = enabled.input.checked
      ? [...modifiers, key.value].join("+")
      : "";
    if (value === "Alt+F4") {
      feedback.textContent =
        "Alt+F4はウィンドウを閉じる操作のため使用できません。";
      return;
    }
    form.disabled = true;
    feedback.textContent = "登録と保存を確認しています…";
    try {
      await save(value);
      saved.textContent = `現在の設定: ${value || "無効"}`;
      feedback.textContent = "保存しました。再起動後もこの設定を使用します。";
    } catch (e) {
      feedback.textContent = e instanceof Error ? e.message : String(e);
    } finally {
      form.disabled = false;
      refreshEnabled();
    }
  };
  load(current);
  form.append(enabled.label, keys, apply, reset, feedback);
  section.append(heading, saved, hint, form);
  return section;
}
function checkbox(text: string, checked: boolean) {
  const label = document.createElement("label");
  const input = document.createElement("input");
  input.type = "checkbox";
  input.value = text;
  input.checked = checked;
  label.append(input, document.createTextNode(text));
  return { label, input };
}
