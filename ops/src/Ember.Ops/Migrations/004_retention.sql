CREATE TABLE activity_retention (
    cohort date NOT NULL,
    week date NOT NULL,
    users int NOT NULL,
    PRIMARY KEY (cohort, week)
);
