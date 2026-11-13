// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using System.Diagnostics.CodeAnalysis;

// Suppress analyzer rules that are not applicable to benchmark/performance test projects
[assembly: SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1035:Do not use APIs banned for analyzers", Justification = "This is a benchmark project, not an analyzer")]