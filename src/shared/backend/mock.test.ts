import { describe, expect, it, vi } from "vitest";
import { createMockBackend } from "./mock";

describe("mock backend", () => {
  it("answers app_info as the browser platform", async () => {
    const backend = createMockBackend();
    await expect(backend.call("app_info")).resolves.toEqual({
      version: "0.1.0",
      platform: "browser",
    });
  });

  it("delivers emitted events until unlistened", async () => {
    const backend = createMockBackend();
    const handler = vi.fn();
    const unlisten = await backend.on("test:event" as never, handler);
    backend.emit("test:event" as never, 1 as never);
    unlisten();
    backend.emit("test:event" as never, 2 as never);
    expect(handler).toHaveBeenCalledExactlyOnceWith(1);
  });
});
