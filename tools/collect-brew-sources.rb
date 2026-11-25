# Collect the exact recipes stored in the installed kegs, including vendored
# resources and patches. The current Homebrew catalog may have newer versions.
require "fileutils"
require "json"
require "formulary"

def copy_resource(resource, destination)
  # Bound the time spent on slow GNU mirrors and retain the recipe checksum.
  if (gnu_path = resource.url[%r{\Ahttps?://(?:ftp|ftpmirror)\.gnu\.org/(?:gnu/)?(.+)\z}, 1])
    raise "Missing GNU source checksum: #{resource.url}" if resource.checksum.nil?

    resource = resource.dup
    resource.url("https://mirrors.kernel.org/gnu/#{gnu_path}")
    resource.mirrors.clear
    resource.mirror("https://mirror.csclub.uwaterloo.ca/gnu/#{gnu_path}")
    resource.mirror("https://ftp.gnu.org/gnu/#{gnu_path}")
  end
  resource.fetch(timeout: 90, skip_patches: true)
  source = resource.cached_download
  FileUtils.mkdir_p(destination)
  if source.directory?
    archive = destination / "source.tar"
    system("git", "-C", source.to_s, "archive", "--format=tar", "-o", archive.to_s, "HEAD", exception: true)
  else
    FileUtils.cp(source, destination / source.basename.to_s.sub(/\A[0-9a-f]{64}--/, ""))
  end
end

script_directory = Pathname.new(__dir__)
root = script_directory.parent
JSON.parse((script_directory / "installed-recipes.json").read).each do |name, recipe|
  formula = if Formulary.respond_to?(:from_path)
    Formulary.from_path(recipe)
  else
    Formulary.factory(recipe)
  end
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
