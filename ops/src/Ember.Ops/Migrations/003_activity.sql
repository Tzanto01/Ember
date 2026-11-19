CREATE TABLE activity_days (
    day date PRIMARY KEY,
    users int NOT NULL,
    active7 int NOT NULL
);

CREATE TABLE activity_weeks (
    week date PRIMARY KEY,
    users int NOT NULL
);
