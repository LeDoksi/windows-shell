import { useEffect, useState } from "react";
import { backend, type AppInfo } from "./shared/backend";

export default function App() {
  const [info, setInfo] = useState<AppInfo | null>(null);

  useEffect(() => {
    backend.call("app_info").then(setInfo);
  }, []);

  return (
    <main className="stub">
      <section className="stub-card">
        <h1>Windows Shell</h1>
        <p>Скелет приложения. Верхняя панель и Dock появятся на следующих этапах.</p>
        {info && (
          <p className="stub-meta">
            v{info.version} ·{" "}
            {info.platform === "browser" ? "браузерный режим, моки" : info.platform}
          </p>
        )}
      </section>
    </main>
  );
}
