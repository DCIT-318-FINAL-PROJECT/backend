# FindMyID backend

ASP.NET Core 10 / C# API with Entity Framework Core and SQLite, connected to the Next.js frontend in `../frontend`.

## Requirements

- .NET 10 SDK (`dotnet --version`)
- Node.js 20.9 or newer and npm for the frontend

SQLite is included through NuGet; no database server installation is required.

## Run the backend

From the project root (`FindMyID`):

```sh
dotnet restore backend/FindMyID.Api.csproj
dotnet run --project backend
```

Or, from this `backend` directory:

```sh
dotnet restore
dotnet run
```

The launch profile sets `ASPNETCORE_ENVIRONMENT=Development` and listens on **http://localhost:5050**. On first run, the app creates `backend/Data/findmyid.db`, its tables, and the development seed data. Subsequent runs retain the database contents.

Check the API:

```sh
curl http://localhost:5050/api/health
```

Expected response: `{"status":"ok"}`.

## Seed the database

Development startup automatically seeds the database. To seed it without starting the HTTP server, run this from the project root:

```sh
dotnet run --project backend -- --seed
```

From the `backend` directory, use `dotnet run -- --seed` instead. The command creates the database if necessary, inserts missing fixtures, and exits. It can be run repeatedly: existing account passwords, preferences, reports, and report statuses are preserved. Ownerless sample reports from the earlier backend are attached to the seed finder accounts. No database deletion is required.

Seeding runs only in **Development**. If bypassing the launch profile, set the environment explicitly:

```sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend --no-launch-profile -- --seed
```

## Seed account login details

All three newly seeded accounts use the password **`FindMyID123!`**.

| Name | Login email | Index number | Sample data |
| --- | --- | --- | --- |
| Ama Mensah | `ama@st.ug.edu.gh` | `22012345` | Finder for Kwame and Sarah; receives a sample handover message |
| Kwame Owusu | `kwame@st.ug.edu.gh` | `23012345` | Finder for Ama and Abena |
| Abena Mansa | `abena@st.ug.edu.gh` | `24012345` | Finder for Marcus |

If an account with one of these emails existed before seeding, its saved password is retained; the seed command does not reset it. The credentials above apply to accounts created by the seeder. These are development demo credentials; they are not added in Production.

The seed also supplies five reports (four Found, one Solved), small placeholder PNG photos, and one handover message. Photos are placeholders rather than real ID cards.

## Connect and run the frontend

Keep the backend running. In a second terminal, from the project root:

```sh
cd frontend
npm install
npm run dev
```

Open **http://localhost:3000/login** and sign in using a seed account. The frontend is already connected: its browser API client calls `/api`, and `frontend/next.config.ts` proxies those requests to **http://localhost:5050**, including the HttpOnly login cookie.

For a different API address, add or update this entry in `frontend/.env.local` (preserve any other entries), then restart the frontend:

```dotenv
API_BASE_URL=http://localhost:5050
```

The default works without an environment file. `API_BASE_URL` belongs to the Next.js server, so it does not need a `NEXT_PUBLIC_` prefix. Rebuild Next.js when changing it for a production build.

Verify the frontend proxy:

```sh
curl http://localhost:3000/api/health
```

This should also return `{"status":"ok"}`. If it fails, check that both services are running, the backend port matches `API_BASE_URL`, and Next.js was restarted after a configuration change.

### Try the seeded flows

1. Log in as **Ama**. My Reports shows Kwame's Found report and Sarah's Solved report. Notifications contains the sample message from Kwame.
2. Search for `22012345` or `Ama`, open Ama's report at Balme Library, and choose Contact Finder. The finder is **Kwame Owusu**. Send a message.
3. Sign out, then log in as **Kwame**. Open Notifications to read the message. My Reports includes Ama and Abena.
4. Kwame can open Ama's report and mark it resolved. Only the finder who submitted a report can resolve it or read its photo.

## Database configuration

The default database is `backend/Data/findmyid.db`, independent of the directory from which you launch the project. To use another SQLite file:

```sh
ConnectionStrings__Database='Data Source=/absolute/path/demo.db' dotnet run --project backend -- --seed
ConnectionStrings__Database='Data Source=/absolute/path/demo.db' dotnet run --project backend
```

Create the parent directory first, and use the same connection string when seeding and running. The schema is initialized with `EnsureCreated`; schema changes require a planned migration. Keep the database and ASP.NET Data Protection keys on persistent storage when deploying. Production requires HTTPS for secure session cookies.

Email verification, password recovery, two-factor authentication, and outbound email/push delivery are not implemented. Messages are available in the app's Notifications screen.

## Verify changes

From the project root:

```sh
dotnet build backend/FindMyID.Api.csproj
cd frontend
npm run typecheck
npm run build
npx playwright test
```

Playwright requires Google Chrome and starts both services on ports 5051 and 3100, using a separate SQLite file at `backend/Data/findmyid-tests.db`.

See [the project README](../README.md) for the API endpoint list and deployment details.
