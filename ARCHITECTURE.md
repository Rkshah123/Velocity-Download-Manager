# Architecture

- `Velocity.App`: WPF presentation layer and composition root.
- `Velocity.Core`: domain models and service contracts.
- `Velocity.Data`: SQLite initialization and persistence infrastructure.
- `Velocity.DownloadEngine`: isolated download engine boundary.
- `Velocity.Services`: application orchestration services.
- `Velocity.BrowserHost`: browser/native-messaging boundary.
- `tests/Velocity.Core.Tests`: automated core tests.

The database is stored under `%LocalAppData%\\Velocity\\velocity.db`.
