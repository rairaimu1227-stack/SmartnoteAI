import { Editor, Extension } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import TaskList from "@tiptap/extension-task-list";
import TaskItem from "@tiptap/extension-task-item";
import Image from "@tiptap/extension-image";
import * as monaco from "monaco-editor";
import EditorWorker from "monaco-editor/editor/editor.worker.js?worker";
import {
  applyProjection,
  assignBlockIds,
  project,
  retainsCanonical,
  type Node,
} from "./projection";
import "./style.css";
(self as unknown as { MonacoEnvironment: unknown }).MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};
type Note = {
  id: string;
  title: string;
  document: Node;
  revision: number;
  createdAt: string;
  updatedAt: string;
  isQuick: boolean;
};
type Task = {
  id: string;
  noteId: string;
  name: string;
  completed: boolean;
  dueAt: string | null;
};
type Prefs = {
  topmost: boolean;
  quickTopmost: boolean;
  autoStart: boolean;
  theme: string;
};
type Tab = {
  id: string;
  mode: "note" | "text";
  view?: monaco.editor.ICodeEditorViewState | null;
  scroll?: number;
  selection?: { from: number; to: number };
};
type Session = { tabs: Tab[]; active: string | null };
type Boot = {
  notes: Note[];
  tasks: Task[];
  preferences: Prefs;
  session: Session | null;
  systemDark: boolean;
};
declare global {
  interface Window {
    chrome: {
      webview: {
        postMessage: (value: unknown) => void;
        addEventListener: (name: string, fn: (e: MessageEvent) => void) => void;
      };
    };
  }
}
const $ = <T extends HTMLElement = HTMLElement>(id: string) =>
  document.getElementById(id) as T;
const pending = new Map<
  string,
  { resolve: (v: any) => void; reject: (e: Error) => void }
>();
function request<T>(command: string, data: unknown = {}): Promise<T> {
  const id = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, command, data });
  });
}
function error(e: unknown) {
  $("error").textContent = e instanceof Error ? e.message : String(e);
}
function status(s: string) {
  $("status").textContent = s;
  void request("status", { text: s }).catch(() => {});
}
let notes: Note[] = [],
  tasks: Task[] = [],
  prefs: Prefs,
  systemDark = false,
  tabs: Tab[] = [],
  active: string | null = null,
  page = "home",
  suppress = false,
  ready = false;
const dirty = new Set<string>(),
  generations = new Map<string, number>(),
  models = new Map<string, monaco.editor.ITextModel>();
let projectionBase: Node | null = null,
  saveTimer: ReturnType<typeof setTimeout> | undefined,
  chain = Promise.resolve(true);
const blockAttribute = Extension.create({
  name: "blockAttribute",
  addGlobalAttributes() {
    return [
      {
        types: ["taskItem"],
        attributes: {
          blockId: {
            default: null,
            parseHTML: (e) => e.getAttribute("data-block-id"),
            renderHTML: (a) => ({ "data-block-id": a.blockId }),
          },
        },
      },
    ];
  },
});
const managedImage = Image.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      attachmentId: {
        default: null,
        parseHTML: (e) => e.getAttribute("data-attachment-id"),
        renderHTML: (a) => ({ "data-attachment-id": a.attachmentId }),
      },
    };
  },
});
const rich = new Editor({
  element: $("rich"),
  extensions: [
    StarterKit.configure({ link: false }),
    TaskList,
    TaskItem.configure({
      nested: true,
      HTMLAttributes: { "data-type": "taskItem" },
      a11y: {
        checkboxLabel: (node) =>
          `チェック項目: ${node.textContent || "空の項目"}`,
      },
    }),
    managedImage,
    blockAttribute,
  ],
  content: { type: "doc", content: [{ type: "paragraph" }] },
  onUpdate: () => {
    if (suppress || !active) return;
    const n = current()!;
    const raw = rich.getJSON() as Node;
    const normalized = assignBlockIds(raw);
    n.document = normalized;
    if (JSON.stringify(raw) !== JSON.stringify(normalized)) {
      const selection = rich.state.selection;
      suppress = true;
      rich.commands.setContent(normalized, { emitUpdate: false });
      rich.commands.setTextSelection({
        from: selection.from,
        to: selection.to,
      });
      suppress = false;
    }
    changed(n.id);
  },
  editorProps: {
    handlePaste: (_view, event) => {
      const image = Array.from(event.clipboardData?.files ?? []).find((f) =>
        /^image\/(png|jpeg)$/.test(f.type),
      );
      if (image) {
        void importImage(image);
        return true;
      }
      return false;
    },
    handleDrop: () => true,
  },
});
monaco.languages.register({ id: "smartnote" });
monaco.languages.setMonarchTokensProvider("smartnote", {
  tokenizer: {
    root: [
      [/^#.*$/, "comment"],
      [/^(text|check):[^ ]+/, "keyword"],
      [/\"(?:[^\"\\]|\\.)*\"/, "string"],
      [/\[[x ]\]/, "number"],
    ],
  },
});
const text = monaco.editor.create($("text"), {
  model: null,
  automaticLayout: true,
  lineNumbers: "on",
  minimap: { enabled: false },
  wordWrap: "on",
  fontSize: 14,
});
text.onDidChangeModelContent(() => {
  if (!suppress && active && tab()?.mode === "text") changed(active);
});
function current() {
  return notes.find((n) => n.id === active);
}
function tab() {
  return tabs.find((t) => t.id === active);
}
function changed(id: string) {
  dirty.add(id);
  generations.set(id, (generations.get(id) ?? 0) + 1);
  status("未保存");
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => void flush(), 1000);
}
function capture() {
  const n = current(),
    t = tab();
  if (!n || !t) return;
  if (t.mode === "text") {
    if (!projectionBase) throw Error("編集用の正本がありません。");
    n.document = applyProjection(projectionBase, text.getValue());
    t.view = text.saveViewState();
  } else {
    t.scroll = $("rich").scrollTop;
    t.selection = {
      from: rich.state.selection.from,
      to: rich.state.selection.to,
    };
  }
}
async function flush(): Promise<boolean> {
  clearTimeout(saveTimer);
  chain = chain.then(async () => {
    try {
      capture();
      status("保存中…");
      for (const id of [...dirty]) {
        const n = notes.find((n) => n.id === id)!;
        const generation = generations.get(id);
        const saved = await request<Note>("saveNote", structuredClone(n));
        n.revision = saved.revision;
        n.createdAt = saved.createdAt;
        n.updatedAt = saved.updatedAt;
        if (generations.get(id) === generation) dirty.delete(id);
      }
      await request("session", { tabs, active });
      tasks = await request<Task[]>("tasks");
      status(dirty.size ? "未保存" : "保存しました");
      $("error").textContent = "";
      return dirty.size === 0;
    } catch (e) {
      status("保存失敗・未保存内容を保持");
      error(e);
      return false;
    }
  });
  return chain;
}
function model(n: Note) {
  let m = models.get(n.id);
  if (!m) {
    m = monaco.editor.createModel(project(n.document), "smartnote");
    models.set(n.id, m);
  }
  return m;
}
function showEditor() {
  const n = current(),
    t = tab();
  if (!n || !t) {
    $("title").setAttribute("disabled", "");
    $<HTMLInputElement>("title").value = "";
    rich.setEditable(false, false);
    suppress = true;
    rich.commands.setContent(
      { type: "doc", content: [{ type: "paragraph" }] },
      { emitUpdate: false },
    );
    text.setModel(null);
    suppress = false;
    $("rich").hidden = false;
    $("text").hidden = true;
    document
      .querySelectorAll<HTMLButtonElement>("[data-format],#image")
      .forEach((b) => (b.disabled = true));
    renderTabs();
    return;
  }
  rich.setEditable(true, false);
  $("title").removeAttribute("disabled");
  $("title").setAttribute("value", n.title);
  $<HTMLInputElement>("title").value = n.title;
  suppress = true;
  const isText = t.mode === "text";
  $("rich").hidden = isText;
  $("text").hidden = !isText;
  $("mode").textContent = isText ? "ノートへ" : "テキストへ";
  $("hint").textContent = isText
    ? "本文: text: の引用符内を編集 ／ チェック: [x]・[ ] ／ 構造・画像・識別子は保護されています。"
    : "画像は貼り付け・画像ボタンで追加できます。チェック項目は保存時にタスクへ登録されます。";
  document
    .querySelectorAll<HTMLButtonElement>("[data-format],#image")
    .forEach((b) => (b.disabled = isText));
  if (isText) {
    projectionBase = structuredClone(n.document);
    const m = model(n);
    if (m.getValue() !== project(n.document)) m.setValue(project(n.document));
    text.setModel(m);
    if (t.view) text.restoreViewState(t.view);
    text.layout();
  } else {
    rich.commands.setContent(n.document, { emitUpdate: false });
    const safe = retainsCanonical(n.document, rich.getJSON());
    rich.setEditable(safe, false);
    document
      .querySelectorAll<HTMLButtonElement>("[data-format],#image")
      .forEach((b) => (b.disabled = !safe));
    if (!safe)
      $("hint").textContent =
        "このノートには未対応の構造・属性があります。ノート側は読み取り専用です。テキストモードで元データを保持して編集できます。";
    if (t.selection) {
      try {
        rich.commands.setTextSelection(t.selection);
      } catch {
        /* Document length may have changed after task sync. */
      }
    }
    $("rich").scrollTop = t.scroll ?? 0;
    projectionBase = null;
  }
  suppress = false;
  renderTabs();
}
async function open(id: string) {
  try {
    capture();
  } catch (e) {
    error(e);
    return;
  }
  if (!tabs.some((t) => t.id === id)) tabs.push({ id, mode: "note" });
  active = id;
  showPage("notes");
  showEditor();
  void persistSession();
}
async function persistSession() {
  try {
    await request("session", { tabs, active });
  } catch (e) {
    error(e);
  }
}
async function closeTab(id: string) {
  if (!(await flush())) return;
  tabs = tabs.filter((t) => t.id !== id);
  models.get(id)?.dispose();
  models.delete(id);
  if (active === id) active = tabs.at(-1)?.id ?? null;
  showEditor();
  renderTabs();
  await persistSession();
}
function button(label: string, fn: () => void) {
  const b = document.createElement("button");
  b.textContent = label;
  b.onclick = fn;
  return b;
}
function renderTabs() {
  const el = $("tabs");
  el.replaceChildren();
  for (const t of tabs) {
    const n = notes.find((n) => n.id === t.id);
    if (!n) continue;
    const wrap = document.createElement("span");
    const b = button(n.title || "無題", () => void open(t.id));
    if (t.id === active) b.className = "active";
    wrap.append(
      b,
      button("×", () => void closeTab(t.id)),
    );
    el.append(wrap);
  }
}
function renderList() {
  const el = $("note-list");
  el.replaceChildren(button("＋ 新しいメモ", () => void create()));
  notes.forEach((n) =>
    el.append(
      button(
        (n.isQuick ? "一時: " : "") + (n.title || "無題"),
        () => void open(n.id),
      ),
    ),
  );
}
function showPage(next: string) {
  page = next;
  for (const p of ["home", "notes", "tasks", "settings"])
    $(p).hidden = p !== next;
  $("heading").textContent = (
    {
      home: "ホーム",
      notes: "メモ",
      tasks: "タスク",
      settings: "設定",
    } as Record<string, string>
  )[next];
  if (next === "home") renderHome();
  if (next === "tasks") renderTasks();
  if (next === "settings") renderSettings();
  if (next === "notes") text.layout();
}
function renderHome() {
  const el = $("home");
  el.replaceChildren();
  const count = document.createElement("div");
  count.className = "card";
  const today = new Date().toLocaleDateString("sv-SE");
  count.textContent = `今日が期限のタスク ${tasks.filter((t) => !t.completed && t.dueAt?.slice(0, 10) === today).length} 件 ／ 未完了 ${tasks.filter((t) => !t.completed).length} 件（PoCは期限設定UIなし）`;
  el.append(
    count,
    button("＋ メモを作成", () => void create()),
  );
  const h = document.createElement("h2");
  h.textContent = "最近編集したメモ";
  el.append(h);
  [...notes]
    .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
    .slice(0, 8)
    .forEach((n) => el.append(button(n.title, () => void open(n.id))));
}
function renderTasks() {
  const el = $("tasks");
  el.replaceChildren();
  if (!tasks.length)
    el.textContent =
      "ノートにチェック項目を追加すると、保存後にここへ表示されます。";
  for (const task of tasks) {
    const row = document.createElement("label");
    row.className = "task";
    const check = document.createElement("input");
    check.type = "checkbox";
    check.checked = task.completed;
    check.onchange = async () => {
      check.disabled = true;
      document.querySelector("main")!.inert = true;
      try {
        if (!(await flush())) {
          check.checked = task.completed;
          return;
        }
        const updated = await request<Note>("toggleTask", {
          id: task.id,
          completed: check.checked,
        });
        const index = notes.findIndex((n) => n.id === updated.id);
        notes[index] = updated;
        if (active === updated.id) showEditor();
        tasks = await request<Task[]>("tasks");
        renderTasks();
      } catch (e) {
        check.checked = task.completed;
        error(e);
      } finally {
        check.disabled = false;
        document.querySelector("main")!.inert = false;
      }
    };
    const label = document.createElement("span");
    label.textContent = task.name || "（空の項目）";
    row.append(
      check,
      label,
      button("メモへ", () => void open(task.noteId)),
    );
    el.append(row);
  }
}
function theme() {
  const dark =
    prefs.theme === "dark" || (prefs.theme === "system" && systemDark);
  document.body.classList.toggle("dark", dark);
  monaco.editor.setTheme(dark ? "vs-dark" : "vs");
}
function renderSettings() {
  const el = $("settings");
  el.replaceChildren();
  for (const [key, label] of [
    ["topmost", "メイン画面を最前面に表示"],
    ["quickTopmost", "クイックメモを最前面に表示"],
    ["autoStart", "Windowsサインイン時に常駐起動"],
  ] as const) {
    const row = document.createElement("label");
    const check = document.createElement("input");
    check.type = "checkbox";
    check.checked = prefs[key];
    check.onchange = async () => {
      try {
        prefs = await request<Prefs>("preferences", {
          ...prefs,
          [key]: check.checked,
        });
      } catch (e) {
        check.checked = prefs[key];
        error(e);
      }
    };
    row.append(check, document.createTextNode(label));
    el.append(row);
  }
  const select = document.createElement("select");
  for (const [v, l] of [
    ["system", "Windowsに追従"],
    ["light", "ライト"],
    ["dark", "ダーク"],
  ]) {
    const o = document.createElement("option");
    o.value = v;
    o.textContent = l;
    select.append(o);
  }
  select.value = prefs.theme;
  select.onchange = async () => {
    try {
      prefs = await request<Prefs>("preferences", {
        ...prefs,
        theme: select.value,
      });
      theme();
    } catch (e) {
      error(e);
    }
  };
  el.append(select);
  const p = document.createElement("p");
  p.textContent =
    "AI・同期は後続フェーズです。ローカル保存のみ実装しています。";
  el.append(p);
}
async function create() {
  try {
    capture();
  } catch (e) {
    error(e);
    return;
  }
  const now = new Date().toISOString();
  const n: Note = {
    id: crypto.randomUUID(),
    title: "新しいメモ",
    document: { type: "doc", content: [{ type: "paragraph" }] },
    revision: 0,
    createdAt: now,
    updatedAt: now,
    isQuick: false,
  };
  notes.unshift(n);
  changed(n.id);
  renderList();
  await open(n.id);
}
async function importImage(file: File) {
  const target = active;
  if (!target || tab()?.mode !== "note") return;
  try {
    if (file.size > 10_000_000) throw Error("画像は10MB以下にしてください。");
    const bytes = new Uint8Array(await file.arrayBuffer());
    let binary = "";
    for (let i = 0; i < bytes.length; i += 32768)
      binary += String.fromCharCode(...bytes.subarray(i, i + 32768));
    const image = await request<{ id: string; src: string }>("image", {
      base64: btoa(binary),
    });
    if (active !== target || tab()?.mode !== "note")
      throw Error("挿入先が変わったため、画像挿入を中止しました。");
    rich
      .chain()
      .focus()
      .insertContent({
        type: "image",
        attrs: { src: image.src, attachmentId: image.id },
      })
      .run();
  } catch (e) {
    error(e);
  }
}
$("mode").onclick = () => {
  try {
    capture();
    const t = tab();
    if (!t) return;
    t.mode = t.mode === "note" ? "text" : "note";
    showEditor();
    void persistSession();
  } catch (e) {
    error(e);
  }
};
$("new").onclick = () => void create();
$("save").onclick = () => void flush();
$("quick").onclick = () => void request("quick").catch(error);
$<HTMLInputElement>("title").oninput = () => {
  const n = current();
  if (!n) return;
  n.title = $<HTMLInputElement>("title").value;
  changed(n.id);
  renderTabs();
  renderList();
};
$("image").onclick = () => $("file").click();
$<HTMLInputElement>("file").onchange = () => {
  const f = $<HTMLInputElement>("file").files?.[0];
  if (f) void importImage(f);
  $<HTMLInputElement>("file").value = "";
};
document.querySelectorAll<HTMLButtonElement>("[data-format]").forEach(
  (b) =>
    (b.onclick = () => {
      const c = rich.chain().focus();
      switch (b.dataset.format) {
        case "bold":
          c.toggleBold().run();
          break;
        case "italic":
          c.toggleItalic().run();
          break;
        case "heading":
          c.toggleHeading({ level: 2 }).run();
          break;
        case "bullet":
          c.toggleBulletList().run();
          break;
        case "task":
          c.toggleTaskList().run();
          break;
        case "up":
          moveChecklist(-1);
          break;
        case "down":
          moveChecklist(1);
          break;
        case "code":
          c.toggleCodeBlock().run();
      }
    }),
);
document.querySelectorAll<HTMLButtonElement>("[data-page]").forEach(
  (b) =>
    (b.onclick = async () => {
      if (await flush()) {
        showPage(b.dataset.page!);
        renderList();
      }
    }),
);
document.addEventListener("keydown", (e) => {
  if (e.ctrlKey && e.key.toLowerCase() === "s") {
    e.preventDefault();
    void flush();
  }
});
window.chrome.webview.addEventListener("message", (e) => {
  const message = e.data;
  if (message.event === "flush") {
    void flush().then((ok) => request("flushed", { token: message.token, ok }));
    return;
  }
  if (message.event === "preferences") {
    prefs = message.data;
    theme();
    if (page === "settings") renderSettings();
    return;
  }
  if (message.event === "systemTheme") {
    systemDark = message.data;
    theme();
    return;
  }
  if (message.event === "refresh") {
    void refresh().catch(error);
    return;
  }
  const item = pending.get(message.id);
  if (!item) return;
  pending.delete(message.id);
  if (message.ok) item.resolve(message.data);
  else item.reject(Error(message.error));
});
async function refresh() {
  if (!ready || !(await flush())) return;
  const data = await request<Boot>("bootstrap");
  let updatedActive = false;
  for (const n of data.notes) {
    const old = notes.findIndex((x) => x.id === n.id);
    if (old < 0) notes.push(n);
    else if (!dirty.has(n.id)) {
      if (n.id === active && notes[old].revision !== n.revision)
        updatedActive = true;
      notes[old] = n;
    }
  }
  if (updatedActive) showEditor();
  tasks = data.tasks;
  renderList();
  if (page === "home") renderHome();
  if (page === "tasks") renderTasks();
}
async function start() {
  try {
    const data = await request<Boot>("bootstrap");
    notes = data.notes;
    tasks = data.tasks;
    prefs = data.preferences;
    systemDark = data.systemDark;
    tabs = (data.session?.tabs ?? []).filter((t) =>
      notes.some((n) => n.id === t.id),
    );
    active = tabs.some((t) => t.id === data.session?.active)
      ? data.session!.active
      : (tabs[0]?.id ?? null);
    theme();
    renderList();
    renderTabs();
    showEditor();
    showPage("home");
    ready = true;
    status("保存済み");
  } catch (e) {
    error(e);
    status("初期化失敗");
  }
}
void start();

function moveChecklist(direction: number) {
  const { $from } = rich.state.selection;
  for (let depth = $from.depth; depth > 1; depth--) {
    if ($from.node(depth).type.name !== "taskItem") continue;
    const parent = $from.node(depth - 1),
      index = $from.index(depth - 1),
      target = index + direction;
    if (
      parent.type.name !== "taskList" ||
      target < 0 ||
      target >= parent.childCount
    )
      return;
    const children = Array.from({ length: parent.childCount }, (_, i) =>
      parent.child(i),
    );
    [children[index], children[target]] = [children[target], children[index]];
    const start = $from.before(depth - 1);
    rich.view.dispatch(
      rich.state.tr.replaceWith(
        start,
        start + parent.nodeSize,
        parent.type.create(parent.attrs, children),
      ),
    );
    rich.commands.setTextSelection(
      start + 3 + children.slice(0, target).reduce((n, c) => n + c.nodeSize, 0),
    );
    rich.commands.focus();
    return;
  }
}
