# SQLRunboard

Run many T-SQL scripts in parallel against one target, and watch them live.

> **Status:** design. There is no code yet. Read the [specification](docs/SPEC.md).

## Purpose

Use SQLRunboard for these jobs:

- Deployment: apply change scripts to a database.
- Data operations: run long loads, rebuilds and maintenance scripts.
- Testing: run many query scripts and compare durations.

SQLRunboard does not keep a deployment journal. If you run a batch again, all included scripts run again.

## Planned features

- A batch file in JSON. You can commit it to git, next to the scripts. It contains no secrets.
- A parallel limit from 1 to 64. A limit of 1 gives an ordered run.
- `GO` batch separators.
- Stop the queue at the first failure, for deployments.
- A GUI in the browser, and a desktop shell with the same page.
- Windows, macOS and Linux, on .NET 10.

## Try the prototype

Open [docs/prototype-gui.html](docs/prototype-gui.html) in a browser. The prototype uses a fake core. It does not connect to a server.

## Contribute

Read [CONTRIBUTING.md](CONTRIBUTING.md). All participants must obey the [Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Do not report a vulnerability in a public issue. Read [SECURITY.md](SECURITY.md).

## Licence

SQLRunboard is available under the [MIT Licence](LICENSE).
