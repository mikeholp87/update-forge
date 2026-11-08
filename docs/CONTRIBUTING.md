# Contributing

Thanks for helping! Please open an issue before large changes.

- Follow [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md); the build treats warnings as errors.
- Add tests for behavior changes, especially anything that builds command lines or decides whether to install.
- Security rules are non-negotiable: no shell-string commands, no integrity-check bypass flags, no silent elevation, no telemetry.
- Do not copy code from incompatibly licensed projects. Report vulnerabilities privately (see SECURITY.md).
- By contributing you agree your work is licensed under MIT.
