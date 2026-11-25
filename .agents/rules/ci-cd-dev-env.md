---
trigger: always_on
description: "B-Lightful Hub CI/CD, Development Environment mockup data, Sonar analysis, and Playwright API/UI testing requirements."
---

# B-Lightful Hub — CI/CD & Development Environment Rules

This rule applies specifically to the **B-Lightful Hub** repository:

1. **Every push & PR runs the full CI/CD pipeline (`.github/workflows/ci-cd.yml`)**:
   - Builds with `SonarAnalyzer.CSharp` enforced via `-warnaserror` (0 warnings allowed) and generates OpenCover coverage (`coverage.opencover.xml`) + optional SonarCloud/SonarQube scan when `SONAR_TOKEN` is configured.
   - Boots the **Development environment** (`ASPNETCORE_ENVIRONMENT=Development`, `B2B_SEED_MOCK_DATA=true`) registered under GitHub's `development` environment with realistic mockup data (`DevMockDataSeeder.cs`).
   - Runs **Playwright API tests** (`tests/e2e/tests/api.spec.ts`) and **Playwright Interface (UI) tests** (`tests/e2e/tests/ui.spec.ts`) against the live Development environment (`/dev-ui`, `/swagger`, `/api/*`, `/graphql`).
2. **Mandatory PR Quality Gate**:
   - Branch protection on `develop` and `main` requires `Build, Unit Tests & Sonar Analysis`, `Deploy Dev Environment & Run Playwright API + UI Tests`, and `Mandatory PR Gate (Tests + Sonar + Dev E2E)` to pass on every commit before a PR can be merged.
3. **Keep Mockup Data & E2E Tests Updated**:
   - Whenever adding new endpoints, domain features, or UI workflows, update `DevMockDataSeeder.cs` (if new mockup data is needed), add unit tests in `tests/B2B.Tests/`, and add/update Playwright API & UI tests in `tests/e2e/tests/`.
   - Always verify locally (`dotnet build -warnaserror`, `dotnet test`, and Playwright E2E tests when applicable) before pushing.
