# AI Boundary

## Baseline

AI is not required for normal use. Default settings set AI, remote AI, and embeddings to OFF.

No model weights are included. No provider is configured.

## Provider contract

`IModelProvider` separates runtime metadata from model output. Model identity must come from provider/runtime configuration, file/hash metadata where applicable, and request metadata. Model self-claims are ignored.

## Context release

`ContextManifest` carries:

- workspace ID;
- artifact;
- selection/range;
- item count;
- classification;
- attachments;
- request ID.

`AIPolicy.release_context` fails when AI is OFF and denies `HEALTH_DATA` by default.

## Actions

Model output is data. Supported action proposals are allowlisted:

- SetCellValue
- SetFormula
- SetNumberFormat
- InsertChart
- ReplaceParagraph
- CreateSlide
- RenameFile

Validation rejects unknown fields, unsupported actions, workspace mismatches, oversized targets/arguments, and malformed objects.

The current code does not provide an action executor wired to a model provider. There is therefore no path from model prose to shell execution.

## Direct access

- Direct model shell access: NO
- Direct model filesystem access: NO
- Direct model network access: NO
- Model approval authority: NO
- Model verification authority: NO
