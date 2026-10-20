export type Node = {
  type: string;
  text?: string;
  attrs?: Record<string, unknown>;
  marks?: { type: string; attrs?: Record<string, unknown> }[];
  content?: Node[];
  [key: string]: unknown;
};
type Entry = {
  line: string;
  node: Node;
  kind: "text" | "empty" | "check" | "fixed";
  prefix: string;
};
function entries(doc: Node): Entry[] {
  const out: Entry[] = [];
  function visit(n: Node, path: string) {
    const fixed = (s: string) =>
      out.push({ line: s, node: n, kind: "fixed", prefix: s });
    fixed(
      `# ${path} ${n.type}${n.type === "image" ? " [保護画像 " + String(n.attrs?.attachmentId) + "]" : ""}`,
    );
    if (n.type === "text") {
      const prefix = `text:${path} `;
      out.push({
        line: prefix + JSON.stringify(n.text ?? ""),
        node: n,
        kind: "text",
        prefix,
      });
    }
    if (
      ["paragraph", "heading", "codeBlock"].includes(n.type) &&
      !n.content?.length
    ) {
      const prefix = `text:${path} `;
      out.push({ line: prefix + '""', node: n, kind: "empty", prefix });
    }
    if (n.type === "taskItem") {
      const prefix = `check:${String(n.attrs?.blockId)} `;
      out.push({
        line: prefix + (n.attrs?.checked ? "[x]" : "[ ]"),
        node: n,
        kind: "check",
        prefix,
      });
    }
    n.content?.forEach((c, i) => visit(c, `${path}.${i}`));
  }
  visit(doc, "0");
  return out;
}
export function project(doc: Node): string {
  return entries(doc)
    .map((e) => e.line)
    .join("\n");
}
export function applyProjection(original: Node, text: string): Node {
  const copy = structuredClone(original);
  const expected = entries(copy),
    lines = text.replace(/\r\n/g, "\n").split("\n");
  if (lines.length !== expected.length)
    throw Error(
      "行の追加・削除はノートモードで行ってください。元データを保持しています。",
    );
  expected.forEach((e, i) => {
    const line = lines[i];
    if (e.kind === "fixed") {
      if (line !== e.line) throw Error("構造・画像の保護行は変更できません。");
      return;
    }
    if (!line.startsWith(e.prefix))
      throw Error(
        "識別子は変更できません。本文の引用符内またはチェック状態のみ編集できます。",
      );
    const value = line.slice(e.prefix.length);
    if (e.kind === "check") {
      if (value !== "[x]" && value !== "[ ]")
        throw Error("チェック状態は [x] または [ ] で指定してください。");
      e.node.attrs!.checked = value === "[x]";
    } else {
      let parsed: unknown;
      try {
        parsed = JSON.parse(value);
      } catch {
        throw Error(
          '本文はJSON文字列として入力してください。改行は \\n、引用符は \\" です。',
        );
      }
      if (typeof parsed !== "string") throw Error("本文は文字列が必要です。");
      if (e.kind === "empty") {
        if (parsed.length > 0)
          e.node.content = [{ type: "text", text: parsed }];
        return;
      }
      if (parsed.length === 0)
        throw Error("空のテキスト要素への変更はノートモードで行ってください。");
      e.node.text = parsed;
    }
  });
  return copy;
}
export function assignBlockIds(doc: Node): Node {
  const copy = structuredClone(doc),
    seen = new Set<string>();
  function visit(n: Node) {
    if (n.type === "taskItem") {
      n.attrs ??= {};
      let id = String(n.attrs.blockId ?? "");
      if (
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
          id,
        ) ||
        seen.has(id)
      )
        id = crypto.randomUUID();
      n.attrs.blockId = id;
      seen.add(id);
    }
    n.content?.forEach(visit);
  }
  visit(copy);
  return copy;
}

// Subset comparison: schema defaults may be added, but canonical keys must never disappear.
export function retainsCanonical(
  original: unknown,
  rendered: unknown,
): boolean {
  if (original === null || typeof original !== "object")
    return original === rendered;
  if (rendered === null || typeof rendered !== "object") return false;
  if (Array.isArray(original))
    return (
      Array.isArray(rendered) &&
      original.length <= rendered.length &&
      original.every((v, i) => retainsCanonical(v, rendered[i]))
    );
  return Object.entries(original).every(([key, value]) =>
    retainsCanonical(value, (rendered as Record<string, unknown>)[key]),
  );
}
