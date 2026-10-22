import { describe, it, expect } from "vitest";
import { readableText, textMemo, isTextMemo } from "./note-tools";
import { applyProjection, project } from "./projection";
describe("independent text memos", () => {
  it("keeps raw code and language independent from the rich document schema", () => {
    const raw = '{"text":"日本語"}\r\n';
    const doc = textMemo(raw, "json");
    expect(isTextMemo(doc)).toBe(true);
    expect(readableText(doc)).toBe(raw);
    expect(doc.attrs?.language).toBe("json");
  });
  it("copies readable headings, checklist and images without modifying the source", () => {
    const source = {
      type: "doc",
      content: [
        {
          type: "heading",
          attrs: { level: 2 },
          content: [{ type: "text", text: "計画" }],
        },
        {
          type: "taskItem",
          attrs: { checked: true, blockId: "original" },
          content: [
            { type: "paragraph", content: [{ type: "text", text: "確認" }] },
          ],
        },
        {
          type: "image",
          attrs: { attachmentId: "keep", src: "original-file" },
        },
      ],
    };
    const before = structuredClone(source);
    expect(readableText(source)).toContain("## 計画");
    expect(readableText(source)).toContain("- [x] 確認");
    expect(readableText(source)).toContain("元のノートに保持");
    expect(source).toEqual(before);
  });
  it("preserves color and highlight attributes through forty protected conversions", () => {
    const doc = {
      type: "doc",
      content: [
        {
          type: "paragraph",
          content: [
            {
              type: "text",
              text: "色付き",
              marks: [
                { type: "textColor", attrs: { color: "#e05260" } },
                { type: "highlight", attrs: { color: "#f4d06f" } },
              ],
            },
          ],
        },
      ],
    };
    let current = structuredClone(doc);
    for (let i = 0; i < 40; i++)
      current = applyProjection(current, project(current)) as typeof doc;
    expect(current).toEqual(doc);
  });
});
