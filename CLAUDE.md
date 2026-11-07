# Nailbiter

Read [AGENTS.md](AGENTS.md) before working in this repository. It owns contribution, fixture, safety and CI/CD instructions.

The backend is a C#/F# .NET 10 application. Shared models/policies are in `apps/backend.shared`; services and Falco.Markup rendering are in `apps/backend.fsharp`; the C# host is in `apps/backend`. F# compile order is explicit in its project file. Frontend updates use SSE/Datastar and vanilla web components. Styles are plain layered CSS in `apps/backend/wwwroot/css` with no build step; see `docs/styling.md`. Marketing is an independent Astro/Cloudflare Worker app.

Before provider changes, follow the domain decisions and provider runbooks. Kalshi ticker dates use US Eastern time, not UTC; do not join games by approximate time or generic team aliases. Provider-format fixtures are independently synthetic, not claimed live responses.
