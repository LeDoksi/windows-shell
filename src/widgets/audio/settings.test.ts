import { describe, expect, it } from "vitest";
import type { AudioState } from "../../shared/backend";
import {
  activeProfile,
  profileFromState,
  shortDeviceName,
  volumeIcon,
  wheelVolume,
  type AudioProfile,
} from "./settings";

const state: AudioState = {
  output: {
    devices: [
      { id: "sony", name: "Наушники (Sony WH-1000XM5)" },
      { id: "realtek", name: "Динамики (Realtek)" },
    ],
    defaultId: "sony",
    volume: 40,
    muted: false,
  },
  input: { devices: [], defaultId: null, volume: 0, muted: false },
};

describe("audio settings", () => {
  it("steps volume with the wheel and clamps", () => {
    expect(wheelVolume(50, -1, 2)).toBe(52);
    expect(wheelVolume(50, 1, 5)).toBe(45);
    expect(wheelVolume(99, -1, 2)).toBe(100);
    expect(wheelVolume(1, 1, 2)).toBe(0);
  });

  it("picks the speaker icon", () => {
    expect(volumeIcon(50, true)).toBe("speakerMuted");
    expect(volumeIcon(0, false)).toBe("speakerMuted");
    expect(volumeIcon(20, false)).toBe("speakerLow");
    expect(volumeIcon(80, false)).toBe("speaker");
  });

  it("shortens device names", () => {
    expect(shortDeviceName("Наушники (Sony WH-1000XM5)")).toBe("Наушники");
    expect(shortDeviceName("(HDMI)")).toBe("(HDMI)");
    expect(shortDeviceName("Speakers")).toBe("Speakers");
  });

  it("makes a profile from the current device and finds the active one", () => {
    const existing: AudioProfile[] = [
      { id: "profile-2", name: "x", deviceId: "realtek", volume: 70, hotkey: null },
    ];
    const profile = profileFromState(state, existing);
    expect(profile).toEqual({
      id: "profile-3",
      name: "Наушники 40%",
      deviceId: "sony",
      volume: 40,
      hotkey: null,
    });
    expect(activeProfile([...existing, profile!], state)).toBe("profile-3");
    expect(activeProfile(existing, state)).toBeNull();
    expect(
      profileFromState({ ...state, output: { ...state.output, defaultId: null } }, []),
    ).toBeNull();
  });
});
