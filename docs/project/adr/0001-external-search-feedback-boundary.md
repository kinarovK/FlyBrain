# ADR 0001: External search policy consumes feedback, never target data

## Status

Accepted for Phase 9 design; implementation awaits design approval.

## Context

Phase 9 searches for a user-selected target while retaining the fixed FlyWire
connectome and frozen external token classifier. A policy that received the
target coordinate, its tokens, or its hemispheres could optimize directly
toward that target and would not demonstrate reward-guided search through the
neural codec.

## Decision

`AutonomousSearchRunner` owns the target and computes the existing Phase 8
reward after an evaluator returns a decoded candidate. `ICoordinateSearchPolicy`
is given no target parameter or target-bearing state. It may consume only its
seeded settings, its prior proposed coordinate, decoded result, reward, angular
error, accepted/rejected state, and bounded feedback history.

The runner asks the policy for a proposal, evaluates it through the frozen
neural pipeline, calculates reward, records an observation, then passes that
observation back to the policy. The random baseline implements the same policy
interface and has the same maximum evaluation budget.

## Consequences

- Policy unit tests can construct feedback without a target and assert the
  public policy contract has no target field or parameter.
- The runner is the only search-layer component that knows both the target and
  decoded coordinate.
- The design does not prevent an evaluator from receiving a proposal; that is
  necessary to encode the candidate and is independent of target secrecy.
- This establishes an external policy/reward loop. It does not make a claim of
  connectome learning or biological navigation.

## Alternatives considered

- Passing target and candidate to the policy: rejected because it leaks the
  answer and reduces the task to direct geographic optimization.
- Passing token-level target feedback: rejected for the same reason and would
  couple policy behavior to the classifier grammar.
- Implementing reward-driven plasticity in Brain Service: rejected as outside
  Phase 9 and incompatible with the fixed-connectome constraint.
