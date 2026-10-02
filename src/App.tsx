import { useEffect, useState } from "react";
import { Desktop } from "./Desktop";
import { backend, type AppInfo } from "./shared/backend";

export default function App() {
  const [info, setInfo] = useState<AppInfo | null>(null);

  useEffect(() => {
    backend.call("app_info").then(setInfo);
  }, []);

  return (
    <>
      <Desktop />
      {info && (
        <p className="app-version">
          Windows Shell v{info.version} ·{" "}
          {info.platform === "browser" ? "браузерный режим, моки" : info.platform}
        </p>
      )}
    </>
  );
}
