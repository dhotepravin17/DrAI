# Load testing

The pilot targets are in PRD §35: queue reads under 500 ms at p95, booking under 1 s at p95, and a burst of
20 bookings per second on one queue with no duplicate tokens.

The automated test `QueueTests` already runs 20 simultaneous bookings against one queue and checks for duplicate
tokens on every `dotnet test`. The k6 script below checks latency against a deployed environment.

## Prerequisites

- [k6](https://k6.io/docs/get-started/installation/) 0.50 or later.
- A **Development or staging** deployment with:
  - `Otp__ExposeCodeInResponse=true` (the script signs in with the development OTP shortcut; this setting only
    works in the Development environment, so never point the script at production).
  - `RateLimits__OtpPerMinutePerIp=100000`, because every virtual user signs in from the same machine.
- A verified doctor whose session for the test date has capacity for the run (the burst alone books 600
  appointments; raise the session capacity in the clinic schedule first).

## Run

```sh
k6 run -e BASE_URL=http://localhost:5221 -e DOCTOR_CODE=NGP-Dr-0001 tests/load/booking-burst.js
```

Optional settings: `READERS` (default 200 concurrent queue readers), `DURATION` (default `2m`), `SESSION_NAME`
(default `Morning`) and `SESSION_DATE` (default today in IST).

k6 exits with a non-zero code when a threshold fails.

## Verify token integrity after the run

```sql
-- Must return no rows.
SELECT QueueSessionId, TokenNumber, COUNT(*) AS Copies
FROM Appointments
GROUP BY QueueSessionId, TokenNumber
HAVING COUNT(*) > 1;
```

The unique index `IX_Appointments_QueueSessionId_TokenNumber` makes duplicates impossible at the database level;
the query confirms it held under load.

## Clean up

Load-test accounts use mobile numbers starting with `99`. Drop the test database or restore it from the
pre-test backup rather than deleting rows by hand.

## Not yet covered

The 5,000 concurrent SignalR connections target needs a WebSocket scenario (`k6/experimental/websockets`) against
`/hubs/queue`. Add it before the pilot sign-off.
