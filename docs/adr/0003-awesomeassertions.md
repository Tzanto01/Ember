# 0003: The .NET tests use AwesomeAssertions 8

Status: accepted, 2026-11-23. Supersedes [0002](0002-fluentassertions-licence.md).

## Context

The FluentAssertions 8 community licence excludes use by or for anyone "that charges fees or earns revenues with or without the intention to make profit". A public tip link for Ember's developer makes it doubtful that the tests may keep using it. AwesomeAssertions is the Apache 2.0 fork of FluentAssertions 8.

## Decision

The three test projects reference AwesomeAssertions 8.x, which keeps the `FluentAssertions` namespace and API.

## Consequences

- No test file changes; only the package references do.
- Version 9 renames the namespace to `AwesomeAssertions`, so that bump touches every test file. It brings nothing Ember needs today.

## Revisit when

A .NET or xunit upgrade needs AwesomeAssertions 9, or version 8 stops working.
