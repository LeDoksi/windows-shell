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

describe("mock config store", () => {
  it("serves defaults and broadcasts accepted changes", async () => {
    const backend = createMockBackend();
    const config = await backend.call("config_get");
    const handler = vi.fn();
    await backend.on("config_changed", handler);
    const next = { ...config, categories: config.categories.slice(1) };
    await backend.call("config_set", { config: next });
    expect(handler).toHaveBeenCalledExactlyOnceWith(next);
    await expect(backend.call("config_get")).resolves.toEqual(next);
  });

  it("rejects invalid configs and keeps the old one", async () => {
    const backend = createMockBackend();
    const config = await backend.call("config_get");
    const broken = { ...config, categories: [config.categories[0], config.categories[0]] };
    await expect(backend.call("config_set", { config: broken })).rejects.toThrow(/повторяется/);
    await expect(backend.call("config_get")).resolves.toEqual(config);
  });
});
