CREATE TABLE comments (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    feedback_id int NOT NULL,
    author text NOT NULL,
    body text NOT NULL CHECK (char_length(body) BETWEEN 1 AND 4000),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX comments_feedback_id ON comments (feedback_id);

CREATE TABLE card_positions (
    feedback_id int PRIMARY KEY,
    position double precision NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE reminder_counters (
    at timestamptz PRIMARY KEY,
    discord_max_id int NOT NULL,
    push_max_id int NOT NULL
);
