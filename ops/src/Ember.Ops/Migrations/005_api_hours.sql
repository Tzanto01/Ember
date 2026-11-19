CREATE TABLE api_hours (
    hour timestamptz NOT NULL,
    route text NOT NULL,
    requests int NOT NULL,
    errors int NOT NULL,
    p50 double precision NOT NULL,
    p95 double precision NOT NULL,
    latency int[] NOT NULL,
    PRIMARY KEY (hour, route)
);
