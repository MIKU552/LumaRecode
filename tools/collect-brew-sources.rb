# Collect the exact recipes stored in the installed kegs, including vendored
# resources and patches. The current Homebrew catalog may have newer versions.
require "fileutils"
require "json"
require "formulary"

def copy_resource(resource, destination)
  resource.fetch
  source = resource.cached_download
  FileUtils.mkdir_p(destination)
  if source.directory?
    archive = destination / "source.tar"
    system("git", "-C", source.to_s, "archive", "--format=tar", "-o", archive.to_s, "HEAD", exception: true)
  else
    FileUtils.cp(source, destination / source.basename.to_s.sub(/\A[0-9a-f]{64}--/, ""))
  end
end

root = Pathname.new(ENV.fetch("LUMA_FFMPEG_SOURCES"))
JSON.parse(ENV.fetch("LUMA_BREW_RECIPES")).each do |name, recipe|
  formula = Formulary.factory(recipe)
  destination = root / name
  FileUtils.mkdir_p(destination)
  FileUtils.cp(recipe, destination)
  spec = formula.stable
  main = spec.resource
  resources = { "upstream" => main }.merge(spec.resources)
  resources.each do |resource_name, resource|
    copy_resource(resource, destination / resource_name)
    resource.patches.grep(ExternalPatch).each_with_index do |patch, index|
      copy_resource(patch.resource, destination / resource_name / "patch-#{index + 1}")
    end
  end
  (spec.patches - main.patches).grep(ExternalPatch).each_with_index do |patch, index|
    copy_resource(patch.resource, destination / "patch-#{index + 1}")
  end
end
