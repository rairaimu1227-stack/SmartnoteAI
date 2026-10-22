import { Mark } from "@tiptap/core";
import type { Node } from "./projection";
export const languages = ["plaintext", "markdown", "powershell", "json", "xml"];
export const isTextMemo = (document: Node) => document.type === "textDocument";
export const textMemo = (text = "", language = "plaintext"): Node => ({
  type: "textDocument",
  text,
  attrs: { language },
});
export function readableText(node: Node): string {
  if (node.type === "textDocument" || node.type === "text")
    return node.text ?? "";
  if (node.type === "image")
    return `[画像: ${node.attrs?.alt || "添付画像（元のノートに保持）"}]\n`;
  const content = (node.content ?? []).map(readableText).join("");
  switch (node.type) {
    case "heading":
      return `${"#".repeat(Number(node.attrs?.level) || 2)} ${content.trimEnd()}\n\n`;
    case "paragraph":
      return content + "\n";
    case "taskItem":
      return `- [${node.attrs?.checked ? "x" : " "}] ${content.trimEnd()}\n`;
    case "listItem":
      return `- ${content.trimEnd()}\n`;
    case "codeBlock":
      return "```" + (node.attrs?.language || "") + "\n" + content + "\n```\n";
    case "hardBreak":
      return "\n";
    default:
      return content;
  }
}
const colorMark = (name: string, tag: string, property: string) =>
  Mark.create({
    name,
    addAttributes() {
      return {
        color: {
          default: null,
          parseHTML: (e) => e.style.getPropertyValue(property) || null,
        },
      };
    },
    parseHTML() {
      return [
        {
          tag: `${tag}[style]`,
          getAttrs: (e) =>
            (e as HTMLElement).style.getPropertyValue(property) ? {} : false,
        },
      ];
    },
    renderHTML({ HTMLAttributes }) {
      const value = String(HTMLAttributes.color || "");
      return [
        tag,
        {
          style: /^(#[0-9a-f]{3,8}|rgb\([\d, .%]+\))$/i.test(value)
            ? `${property}:${value}`
            : "",
        },
        0,
      ];
    },
  });
export const TextColor = colorMark("textColor", "span", "color");
export const Highlight = colorMark("highlight", "mark", "background-color");
