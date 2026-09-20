# Local Authentication

TradingFlow uses ASP.NET Core Identity with the existing SQLite database. There
is no public registration endpoint. Passwords are passed directly to Identity
and the database stores `PasswordHash`; there is no plaintext password column.

## Create The First Administrator

Stop the web host, then run this command from the repository root in an
interactive terminal:

```powershell
dotnet run --project src\TradingFlow.Web -- users add --username YOUR_NAME --admin
```

Optional display name:

```powershell
dotnet run --project src\TradingFlow.Web -- users add --username YOUR_NAME --display-name "Your Name" --admin
```

The command asks for the password and confirmation using masked console input.
It refuses redirected input so a password cannot accidentally be supplied in a
script or shell history. Passwords must contain at least 12 characters with an
uppercase letter, lowercase letter, digit, and non-alphanumeric character.

After signing in, administrators can use `/Users` to add more accounts. A user
without the Administrator role can use protected operator features but cannot
open user administration.

## Access Boundary

Anonymous access is limited to:

- `/Earnings`
- earnings calendar/status GET APIs under `/api/earnings` and
  `/api/v1/earnings`
- `/Login` and `/AccessDenied`, which are required to authenticate
- static UI assets

The earnings refresh POST operation is authenticated because it performs
provider work. Every trading, paper, backtest, research, wishlist, news,
automation, account, order, health, and other operational page or API uses the
authenticated fallback policy.

Browser sessions use an HTTP-only, same-site cookie with an eight-hour sliding
lifetime. API callers receive `401` or `403` instead of an HTML redirect. The
Android app keeps its authentication cookie in memory and offers sign-in from
the More screen; it never persists the supplied password.

Five failed password attempts lock the account for 15 minutes. User-facing
login failures are deliberately generic and do not reveal whether an account
exists.
