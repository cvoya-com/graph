# Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
# See LICENSE in the project root for full license terms.

require "set"
require "yaml"

ROOT = File.expand_path("../..", __dir__)
CI_WORKFLOW_PATH = File.join(ROOT, ".github/workflows/ci.yml")
RELEASE_WORKFLOW_PATH = File.join(ROOT, ".github/workflows/release.yml")
SOLUTION_PATH = File.join(ROOT, "cvoya-graph.sln")
PROJECT_ROOTS = %w[src tests examples eng].freeze
# This example is the release package consumer. Adding it to the solution would
# make the normal Release build consume packages before the release job packs
# them, so the release workflow validates it separately after packing.
SOLUTION_PROJECT_EXCLUSIONS = Set[
  "examples/SimpleMovieExample/SimpleMovieExample.csproj"
].freeze

def check(condition, message)
  raise message unless condition
end

def normalize_path(path)
  path.tr("\\", "/").sub(%r{\A\./}, "")
end

def load_workflow(path)
  YAML.safe_load(File.read(path, encoding: Encoding::UTF_8), aliases: true)
end

def workflow_steps(workflow)
  workflow.fetch("jobs").values.flat_map { |job| job.fetch("steps", []) }
end

def step_named(workflow, name)
  step = workflow_steps(workflow).find { |candidate| candidate["name"] == name }
  raise "workflow step named #{name.inspect} is missing" unless step

  step
end

def project_references(text, root)
  text.to_s.scan(%r{#{Regexp.escape(root)}/[A-Za-z0-9_.\-/]+\.csproj}).map { |path| normalize_path(path) }.to_set
end

def check_inventory(label, expected, actual)
  return if expected == actual

  missing = expected - actual
  extra = actual - expected
  details = []
  details << "missing: #{missing.to_a.sort.join(', ')}" unless missing.empty?
  details << "unexpected: #{extra.to_a.sort.join(', ')}" unless extra.empty?
  raise "#{label} is out of sync (#{details.join('; ')})"
end

Dir.chdir(ROOT) do
  repository_projects = PROJECT_ROOTS
    .flat_map { |root| Dir.glob("#{root}/**/*.csproj") }
    .reject { |path| path.include?("/bin/") || path.include?("/obj/") }
    .map { |path| normalize_path(path) }
    .to_set

  check(
    SOLUTION_PROJECT_EXCLUSIONS.subset?(repository_projects),
    "solution-project exclusions contain a project that no longer exists"
  )

  solution_projects = File.read(SOLUTION_PATH, encoding: Encoding::UTF_8)
    .scan(/"([^"]+\.csproj)"/)
    .flatten
    .map { |path| normalize_path(path) }
    .to_set

  check_inventory(
    "cvoya-graph.sln project inventory",
    repository_projects - SOLUTION_PROJECT_EXCLUSIONS,
    solution_projects
  )

  release = load_workflow(RELEASE_WORKFLOW_PATH)
  release_runs = workflow_steps(release).map { |step| step["run"] }.compact.join("\n")
  release_test_projects = project_references(release_runs, "tests")
  expected_release_tests = repository_projects
    .select { |path| path.start_with?("tests/") && !path.include?("Performance.Tests") }
    .to_set

  check_inventory("release test-project inventory", expected_release_tests, release_test_projects)

  expected_release_sources = repository_projects.select { |path| path.start_with?("src/") }.to_set
  release_build_projects = project_references(step_named(release, "Build packages (Release)").fetch("run"), "src")
  release_pack_projects = project_references(step_named(release, "Pack").fetch("run"), "src")

  check_inventory("release package-build inventory", expected_release_sources, release_build_projects)
  check_inventory("release pack inventory", expected_release_sources, release_pack_projects)

  ci = load_workflow(CI_WORKFLOW_PATH)
  filter_step = ci.fetch("jobs").fetch("changes").fetch("steps").find { |step| step["id"] == "filter" }
  check(filter_step, "CI path-filter step is missing")

  filters = YAML.safe_load(filter_step.fetch("with").fetch("filters"))
  match_flags = File::FNM_PATHNAME | File::FNM_EXTGLOB
  selected_scopes = lambda do |path|
    filters.each_with_object(Set.new) do |(scope, patterns), selected|
      selected << scope if patterns.any? { |pattern| File.fnmatch?(pattern, path, match_flags) }
    end
  end

  scope_examples = {
    "src/Graph.Future/NewType.cs" => %w[dotnet packages],
    "src/Graph.Future/Graph.Future.csproj" => %w[dotnet packages],
    "tests/Graph.Future.Tests/NewTests.cs" => %w[dotnet],
    "tests/Graph.Future.Tests/Graph.Future.Tests.csproj" => %w[dotnet],
    "examples/Future/Future.csproj" => %w[dotnet],
    "eng/future-validator/check.rb" => %w[dotnet packages],
    "scripts/future-validation-tool" => %w[dotnet packages],
    ".github/workflows/future-validation.yml" => %w[dotnet packages workflow_files],
    "Directory.Future.props" => %w[dotnet packages],
    "assets/icon.png" => %w[packages],
    "docs/future-design.md" => []
  }

  scope_examples.each do |path, expected_scopes|
    check_inventory(
      "CI validation scopes for #{path}",
      expected_scopes.to_set,
      selected_scopes.call(path)
    )
  end
end

puts "Validation project inventories and CI path scopes passed."
