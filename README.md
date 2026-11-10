# School Admission System

.NET 8 (minimal API + EF Core) · PostgreSQL · React 18 (Vite) · Docker



A school online admission system where students apply for a limited number of seats on a first-come-first-served basis. Applying holds a seat for a limited time (HOLD_DURATION_SECONDS, default 600). If the admission fee is paid in time, the admission is confirmed. Otherwise the seat is released automatically for others.

Highlights:
- Concurrency-safe seat booking using PostgreSQL transactions and row-level locks, so seats are never oversold, even with multiple backend processes
- Seats are released on expiry with no background job, because expiry is evaluated from the hold's expiresAt time
- Idempotent confirm and cancel
- React UI with live seat counts and a countdown, and a student's active applications survive page refresh
- Automated backend tests, including a concurrency test
- Runs entirely with a single `docker compose up`

Stack: C# / .NET 8 (minimal API, EF Core), PostgreSQL 16, React 18 (Vite), Docker Compose.
