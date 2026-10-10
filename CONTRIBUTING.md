# Contributing to SQLRunboard

Thank you for your help. This guide tells you how to contribute.

## Before you start

- Read the [specification](docs/SPEC.md). It records the design decisions.
- For a large change, open an issue first. Get agreement on the design before you write code.
- For a small fix, open a pull request directly.

## Report a bug

1. Search the existing issues first.
2. Open a new issue with the bug report template.
3. Give the steps, the expected result and the actual result.
4. Give your operating system, the SQLRunboard version and the SQL Server version.

Do not report a security vulnerability in a public issue. Read [SECURITY.md](SECURITY.md).

## Development setup

You need these tools:

- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Docker, for the integration tests. The repository will supply a `compose.yaml` with SQL Server 2022. On Apple Silicon, turn on Rosetta emulation in Docker Desktop.

Set `SQLRUNBOARD_TEST_SQL` to the connection string of your test server. The integration tests fail if the variable is not set.

## Pull requests

1. Fork the repository and make a branch from `main`.
2. Keep each pull request to one subject.
3. Add or update the tests for your change.
4. Make sure that the build and all tests pass on your computer.
5. Update `docs/SPEC.md` if your change alters a design decision.
6. Open the pull request and fill in the template.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

- Use one of these prefixes: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`.
- Add a scope when the change is in one component, for example `feat(gui): ...`.
- Write a subject line of 100 characters or fewer.

## Code style

- Obey the rules in `.editorconfig`.
- All code must run on Windows, macOS and Linux.
- Do not hardcode absolute paths.
- Ask in an issue before you add a dependency.

## Licence

When you contribute, you agree that your contribution is available under the [MIT Licence](LICENSE).
