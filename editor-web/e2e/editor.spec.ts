import { test, expect, type Page } from "@playwright/test";
const id = "0ec9b382-2707-44e3-b5d8-bd49dd6ab40a";
async function boot(page: Page, unknown = false) {
  await page.route("https://media.smartnote.local/**", (route) =>
    route.fulfill({
      contentType: "image/png",
      body: Buffer.from(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aPrsAAAAASUVORK5CYII=",
        "base64",
      ),
    }),
  );
  await page.addInitScript(
    ({ id, unknown }) => {
      const w = window as any;
      let receive: (event: any) => void;
      const stamp = "2026-10-20T00:00:00Z";
      const initial = Array.from({ length: 10 }, (_, i) => ({
        id: `00000000-0000-4000-8000-${String(i).padStart(12, "0")}`,
        title: `検証メモ ${i}`,
        revision: 1,
        createdAt: stamp,
        updatedAt: stamp,
        isQuick: false,
        document: {
          type: "doc",
          ...(unknown ? { attrs: { future: "retain" } } : {}),
          content: [
            {
              type: "paragraph",
              content: [
                { type: "text", text: `本文 ${i}`, marks: [{ type: "bold" }] },
              ],
            },
            {
              type: "taskList",
              content: [
                {
                  type: "taskItem",
                  attrs: { blockId: id, checked: false },
                  content: [
                    {
                      type: "paragraph",
                      content: [{ type: "text", text: "テストタスク" }],
                    },
                  ],
                },
              ],
            },
            {
              type: "image",
              attrs: {
                attachmentId: id,
                src: `https://media.smartnote.local/${id}.img`,
              },
            },
          ],
        },
      }));
      let notes =
        JSON.parse(localStorage.getItem("test-notes") || "null") || initial;
      let session = JSON.parse(
        localStorage.getItem("test-session") || "null",
      ) || {
        tabs: initial.map((n) => ({ id: n.id, mode: "note" })),
        active: initial[0].id,
      };
      let preferences = {
        topmost: false,
        quickTopmost: true,
        autoStart: false,
        theme: "system",
      };
      const tasks = () =>
        notes.map((n: any) => ({
          id: n.id,
          noteId: n.id,
          name: n.document.content[1].content[0].content[0].content[0].text,
          completed: n.document.content[1].content[0].attrs.checked,
          dueAt: null,
        }));
      w.__test = {
        notes: () => notes,
        failSave: false,
        externalChange: () => {
          notes[0].document.content[0].content[0].text =
            "外部ウィンドウから更新";
          notes[0].revision++;
          receive({ data: { event: "refresh" } });
        },
      };
      w.chrome ??= {};
      w.chrome.webview = {
        addEventListener: (_name: string, fn: any) => (receive = fn),
        postMessage: async (m: any) => {
          try {
            let data: any = null;
            switch (m.command) {
              case "bootstrap":
                data = {
                  notes,
                  tasks: tasks(),
                  preferences,
                  session,
                  systemDark: false,
                };
                break;
              case "tasks":
                data = tasks();
                break;
              case "saveNote":
                if (w.__test.failSave) throw Error("注入した保存失敗");
                data = {
                  ...structuredClone(m.data),
                  revision: m.data.revision + 1,
                };
                notes = notes.map((n: any) => (n.id === data.id ? data : n));
                localStorage.setItem("test-notes", JSON.stringify(notes));
                break;
              case "session":
                session = m.data;
                localStorage.setItem("test-session", JSON.stringify(session));
                break;
              case "toggleTask":
                data = notes.find((n: any) => n.id === m.data.id);
                data.document.content[1].content[0].attrs.checked =
                  m.data.completed;
                data.revision++;
                localStorage.setItem("test-notes", JSON.stringify(notes));
                break;
              case "preferences":
                preferences = m.data;
                data = preferences;
                break;
              default:
                break;
            }
            queueMicrotask(() =>
              receive({ data: structuredClone({ id: m.id, ok: true, data }) }),
            );
          } catch (e) {
            receive({ data: { id: m.id, ok: false, error: String(e) } });
          }
        },
      };
    },
    { id, unknown },
  );
  await page.goto("/index.html");
  await expect(page.locator("#status")).toHaveText("保存済み");
  await page.locator("[data-page=notes]").click();
}
async function value(page: Page) {
  return page.evaluate(() => {
    const n = (window as any).__test.notes()[0];
    return n;
  });
}
test("Tiptap editing, checklist task sync, image and ten tabs survive navigation/reload", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await boot(page);
  await expect(page.locator("#tabs>span")).toHaveCount(10);
  for (let i = 0; i < 10; i++) {
    await page
      .locator("#tabs>span")
      .nth(i)
      .getByRole("button", { name: `検証メモ ${i}`, exact: true })
      .click();
    await expect(page.locator("#title")).toHaveValue(`検証メモ ${i}`);
    await expect(page.locator(".tiptap>p").first()).toHaveText(`本文 ${i}`);
  }
  await page
    .locator("#tabs>span")
    .first()
    .getByRole("button", { name: "検証メモ 0", exact: true })
    .click();

  await expect(page.locator("#rich img")).toBeVisible();
  await expect
    .poll(() =>
      page
        .locator("#rich img")
        .evaluate((img: HTMLImageElement) => img.naturalWidth),
    )
    .toBe(1);
  const para = page.locator(".tiptap>p").first();
  await para.click();
  await page.keyboard.press("End");
  await page.keyboard.insertText(" 更新");
  await page.locator("#save").click();
  await expect(page.locator("#status")).toHaveText("保存しました");
  expect(JSON.stringify((await value(page)).document)).toContain("更新");
  await page.locator("#rich input[type=checkbox]").check();
  await page.locator("[data-page=tasks]").click();
  await expect(page.locator("#tasks input").first()).toBeChecked();
  await page.locator("#tasks input").first().uncheck();
  await expect(page.locator("#tasks input").first()).not.toBeChecked();
  await page.locator("[data-page=home]").click();
  await page.locator("[data-page=notes]").click();
  await expect(page.locator("#tabs>span")).toHaveCount(10);
  await expect(page.locator("#rich input")).not.toBeChecked();
  await page.reload();
  await expect(page.locator("#status")).toHaveText("保存済み");
  await page.locator("[data-page=notes]").click();
  await expect(page.locator("#tabs>span")).toHaveCount(10);
  await expect(page.locator(".tiptap>p").first()).toContainText("更新");
  expect(errors).toEqual([]);
});
test("40 real mode transitions preserve image, marks, checked and blockId", async ({
  page,
}) => {
  await boot(page);
  const before = (await value(page)).document;
  for (let i = 0; i < 40; i++) await page.locator("#mode").click();
  await page.locator("#save").click();
  await expect(page.locator("#status")).toHaveText("保存しました");
  expect((await value(page)).document).toEqual(before);
});
test("Monaco accepts text edits, rejects structural edits and retains invalid buffer", async ({
  page,
}) => {
  await boot(page);
  await page.locator("#mode").click();
  await page.locator(".monaco-editor").click();
  await page.keyboard.press("Control+Home");
  await page.keyboard.press("Control+f");
  await page.keyboard.insertText("本文 0");
  await page.keyboard.press("Escape");
  // Use Monaco's visible view to locate text via Ctrl+H keyboard find/replace.
  await page.keyboard.press("Control+Home");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("End");
  await page.keyboard.press("ArrowLeft");
  await page.keyboard.insertText(" 編集");
  await page.locator("#save").click();
  await expect(page.locator("#status")).toHaveText("保存しました");
  expect(JSON.stringify((await value(page)).document)).toContain("編集");
  const saved = (await value(page)).document;
  await page.locator(".monaco-editor").click();
  await page.keyboard.press("Control+Home");
  await page.keyboard.insertText("BROKEN");
  await page.locator("#mode").click();
  await expect(page.locator("#text")).toBeVisible();
  await expect(page.locator("#error")).toContainText("保護行");
  await page.locator("#save").click();
  await expect(page.locator("#status")).toContainText("保存失敗");
  expect((await value(page)).document).toEqual(saved);
  await expect(page.locator(".view-lines")).toContainText("BROKEN");
});
test("save failure retains draft and refuses tab close; retry saves", async ({
  page,
}) => {
  await boot(page);
  await page.evaluate(() => {
    (window as any).__test.failSave = true;
  });
  await page.locator("#title").fill("未保存タイトル");
  await page
    .locator("#tabs>span")
    .first()
    .getByRole("button", { name: "×", exact: true })
    .click();
  await expect(page.locator("#error")).toContainText("保存失敗");
  await expect(page.locator("#tabs>span")).toHaveCount(10);
  await expect(page.locator("#title")).toHaveValue("未保存タイトル");
  await page.evaluate(() => {
    (window as any).__test.failSave = false;
  });
  await page.locator("#save").click();
  await expect(page.locator("#status")).toHaveText("保存しました");
  expect((await value(page)).title).toBe("未保存タイトル");
});
test("unknown canonical attributes make rich editor read-only but survive text mode", async ({
  page,
}) => {
  await boot(page, true);
  await expect(page.locator(".tiptap")).toHaveAttribute(
    "contenteditable",
    "false",
  );
  await expect(page.locator("#hint")).toContainText("未対応");
  const before = (await value(page)).document;
  await page.locator("#mode").click();
  await page.locator("#mode").click();
  await page.locator("#save").click();
  await expect(page.locator("#status")).toHaveText("保存しました");
  expect((await value(page)).document).toEqual(before);
});

test("checklist reorder toolbar preserves existing BlockId and assigns one to a new item", async ({
  page,
}) => {
  await boot(page);
  await page
    .locator("#rich li[data-type=taskItem] p")
    .click({ position: { x: 8, y: 8 } });
  await expect
    .poll(() =>
      page.evaluate(() =>
        Boolean(
          window
            .getSelection()
            ?.anchorNode?.parentElement?.closest("li[data-type=taskItem]"),
        ),
      ),
    )
    .toBe(true);
  await page.keyboard.press("End");
  await page.keyboard.press("Enter");
  await page.keyboard.insertText("第二タスク");
  await expect(page.locator("#rich li[data-type=taskItem]")).toHaveCount(2);
  await page.locator("[data-format=up]").click();
  await expect(
    page.locator("#rich li[data-type=taskItem]").first(),
  ).toContainText("第二タスク");
  await page.locator("#save").click();
  await expect(page.locator("#status")).toHaveText("保存しました");
  const items = (await value(page)).document.content[1].content;
  expect(items[1].attrs.blockId).toBe(id);
  expect(items[0].attrs.blockId).not.toBe(id);
  await page.locator("#mode").click();
  await page.locator("#mode").click();
  await expect(
    page.locator("#rich li[data-type=taskItem]").first(),
  ).toContainText("第二タスク");
});

test("external quick-memo update refreshes the active clean editor before further edits", async ({
  page,
}) => {
  await boot(page);
  await page.evaluate(() => (window as any).__test.externalChange());
  await expect(page.locator(".tiptap>p").first()).toHaveText(
    "外部ウィンドウから更新",
  );
  await page.locator(".tiptap>p").first().click();
  await page.keyboard.press("End");
  await page.keyboard.insertText(" 続き");
  await page.locator("#save").click();
  await expect(page.locator("#status")).toHaveText("保存しました");
  expect(JSON.stringify((await value(page)).document)).toContain(
    "外部ウィンドウから更新 続き",
  );
});
