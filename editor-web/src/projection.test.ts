import { describe, it, expect } from "vitest";
import {
  applyProjection,
  project,
  assignBlockIds,
  type Node,
} from "./projection";
const id = "0ec9b382-2707-44e3-b5d8-bd49dd6ab40a";
const fixture = (): Node => ({
  type: "doc",
  attrs: { custom: "keep" },
  content: [
    {
      type: "heading",
      attrs: { level: 2 },
      content: [
        {
          type: "text",
          text: '日本語と "引用"\n次の行',
          marks: [{ type: "bold" }, { type: "italic" }],
        },
      ],
    },
    {
      type: "bulletList",
      content: [
        {
          type: "listItem",
          content: [
            {
              type: "paragraph",
              content: [{ type: "text", text: "箇条書き" }],
            },
          ],
        },
      ],
    },
    {
      type: "taskList",
      content: [
        {
          type: "taskItem",
          attrs: { blockId: id, checked: true, custom: { preserve: 42 } },
          content: [
            { type: "paragraph", content: [{ type: "text", text: "タスク" }] },
          ],
        },
      ],
    },
    {
      type: "codeBlock",
      attrs: { language: "powershell" },
      content: [{ type: "text", text: "Get-Process\n# sample" }],
    },
    {
      type: "image",
      attrs: {
        attachmentId: id,
        src: `https://media.smartnote.local/${id}.img`,
        alt: "画像",
      },
    },
    {
      type: "table",
      attrs: { unknown: 123 },
      content: [
        {
          type: "tableRow",
          content: [
            {
              type: "tableCell",
              content: [
                { type: "paragraph", content: [{ type: "text", text: "表" }] },
              ],
            },
          ],
        },
      ],
    },
  ],
});
describe("POC-005/006/007/008: lossless projection", () => {
  it("retains all nodes, marks, custom attributes, images and IDs across 40 round trips", () => {
    const original = fixture();
    let doc = structuredClone(original);
    for (let i = 0; i < 40; i++) doc = applyProjection(doc, project(doc));
    expect(doc).toEqual(original);
  });
  it("patches only text and checkbox; original is untouched", () => {
    const original = fixture();
    const updated = applyProjection(
      original,
      project(original)
        .replace('"タスク"', '"更新したタスク"')
        .replace(`check:${id} [x]`, `check:${id} [ ]`),
    );
    expect(updated.content![2].content![0].attrs).toEqual({
      blockId: id,
      checked: false,
      custom: { preserve: 42 },
    });
    expect(original.content![2].content![0].attrs!.checked).toBe(true);
    expect(updated.content![4]).toEqual(original.content![4]);
  });
  it.each(["delete", "identity", "image", "syntax", "check", "append"])(
    "rejects unsafe %s edits without modifying canonical JSON",
    (kind) => {
      const original = fixture(),
        before = JSON.stringify(original);
      let value = project(original);
      if (kind === "delete") value = value.split("\n").slice(1).join("\n");
      if (kind === "identity")
        value = value.replace(`check:${id}`, `check:invalid`);
      if (kind === "image") value = value.replace("[保護画像", "[変更画像");
      if (kind === "syntax") value = value.replace('"タスク"', "not JSON");
      if (kind === "check") value = value.replace("[x]", "[y]");
      if (kind === "append") value += "\nextra";
      expect(() => applyProjection(original, value)).toThrow();
      expect(JSON.stringify(original)).toBe(before);
    },
  );
  it("keeps escaped newlines, Unicode, quotes and CRLF source handling", () => {
    const doc = fixture();
    expect(applyProjection(doc, project(doc).replace(/\n/g, "\r\n"))).toEqual(
      doc,
    );
  });
  it("keeps block IDs on reorder and text changes; replaces pasted duplicates", () => {
    const doc = fixture();
    const item = doc.content![2].content![0];
    doc.content![2].content!.push(structuredClone(item));
    const normalized = assignBlockIds(doc);
    expect(normalized.content![2].content![0].attrs!.blockId).toBe(id);
    expect(normalized.content![2].content![1].attrs!.blockId).not.toBe(id);
    normalized.content![2].content!.reverse();
    expect(assignBlockIds(normalized)).toEqual(normalized);
  });
  it("keeps independent state for ten documents", () => {
    const docs = Array.from({ length: 10 }, (_, i) => {
      const doc = fixture();
      doc.content![0].content![0].text = `メモ ${i}`;
      return doc;
    });
    for (let cycle = 0; cycle < 20; cycle++)
      docs.forEach((doc, i) => {
        docs[i] = applyProjection(doc, project(doc));
      });
    docs.forEach((doc, i) =>
      expect(doc.content![0].content![0].text).toBe(`メモ ${i}`),
    );
  });
});

describe("canonical guards", () => {
  it("rejects a zero-length text node without changing the source", () => {
    const doc = fixture();
    expect(() =>
      applyProjection(doc, project(doc).replace('"タスク"', '""')),
    ).toThrow();
    expect(doc.content![2].content![0].content![0].content![0].text).toBe(
      "タスク",
    );
  });
});

it("edits an empty paragraph without losing paragraph attributes", () => {
  const doc: Node = {
    type: "doc",
    content: [{ type: "paragraph", attrs: { future: "keep" } }],
  };
  const edited = applyProjection(
    doc,
    project(doc).replace('""', '"最初の本文"'),
  );
  expect(edited.content![0]).toEqual({
    type: "paragraph",
    attrs: { future: "keep" },
    content: [{ type: "text", text: "最初の本文" }],
  });
  expect(applyProjection(doc, project(doc))).toEqual(doc);
});
