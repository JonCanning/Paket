/// Contains methods for the update process.
module Paket.UpdateProcess

open Paket
open System.IO
open Paket.Domain
open Paket.PackageResolver
open System.Collections.Generic
open Chessie.ErrorHandling
open Paket.Logging

let selectiveUpdate force getSha1 getSortedVersionsF getPackageDetailsF (lockFile:LockFile) (dependenciesFile:DependenciesFile) updateMode semVerUpdateMode =
    let allVersions = Dictionary<PackageName,SemVerInfo list>()
    let getSortedAndCachedVersionsF sources resolverStrategy groupName packageName =
        match allVersions.TryGetValue(packageName) with
        | false,_ ->
            let versions = 
                verbosefn "  - fetching versions for %O" packageName
                getSortedVersionsF sources resolverStrategy groupName packageName

            if Seq.isEmpty versions then
                failwithf "Couldn't retrieve versions for %O." packageName
            allVersions.Add(packageName,versions)
            versions
        | true,versions -> versions 
        |> List.toSeq
        

    let getPreferredVersionsF preferredVersions changedDependencies sources resolverStrategy groupName packageName = 
        seq { 
            match preferredVersions |> Map.tryFind (groupName, packageName), resolverStrategy, changedDependencies |> Set.exists ((=) (groupName, packageName)) with
            | Some v, ResolverStrategy.Min, _
            | Some v, _, false -> yield v
            | _ -> ()
            yield! getSortedAndCachedVersionsF sources resolverStrategy groupName packageName
        }

    let dependenciesFile =
        let processFile createRequirementF =
          lockFile.GetGroupedResolution()
          |> Map.fold (fun (dependenciesFile:DependenciesFile) (groupName,packageName) resolvedPackage -> 
                             dependenciesFile.AddFixedPackage(groupName,packageName,createRequirementF resolvedPackage.Version)) dependenciesFile
    
        let formatPrerelease (v:SemVerInfo) =
            match v.PreRelease with
            | Some p -> sprintf " %O" p
            | None -> ""

        match semVerUpdateMode with
        | SemVerUpdateMode.NoRestriction -> dependenciesFile
        | SemVerUpdateMode.KeepMajor -> processFile (fun v -> sprintf "~> %d.%d" v.Major v.Minor + formatPrerelease v)
        | SemVerUpdateMode.KeepMinor -> processFile (fun v -> sprintf "~> %d.%d.%d" v.Major v.Minor v.Patch + formatPrerelease v)
        | SemVerUpdateMode.KeepPatch -> processFile (fun v -> sprintf "~> %d.%d.%d.%s" v.Major v.Minor v.Patch v.Build + formatPrerelease v)

    let getVersionsF,groupsToUpdate =
        let changes,groups =
            match updateMode with
            | UpdateAll ->
                let changes =
                    lockFile.GetGroupedResolution()
                    |> Seq.map (fun k -> k.Key)
                    |> Set.ofSeq

                changes,dependenciesFile.Groups
            | UpdateGroup groupName ->
                let changes =
                    lockFile.GetGroupedResolution()
                    |> Seq.map (fun k -> k.Key)
                    |> Seq.filter (fun (g,_) -> g = groupName)
                    |> Set.ofSeq

                let groups =
                    dependenciesFile.Groups
                    |> Map.filter (fun k _ -> k = groupName)

                changes,groups
            | UpdateFiltered (groupName, filter) ->
                let changes =
                    lockFile.GetGroupedResolution()
                    |> Seq.map (fun k -> k.Key)
                    |> Seq.filter (fun (g,_) -> g = groupName)
                    |> Seq.filter (fun (_, p) -> filter.Match p)
                    |> Set.ofSeq

                let groups =
                    dependenciesFile.Groups
                    |> Map.filter (fun k _ -> k = groupName)

                changes,groups
            | Install ->
                let nuGetChanges = DependencyChangeDetection.findNuGetChangesInDependenciesFile(dependenciesFile,lockFile)
                let nuGetChangesPerGroup =
                    nuGetChanges
                    |> Seq.groupBy fst
                    |> Map.ofSeq

                let remoteFileChanges = DependencyChangeDetection.findRemoteFileChangesInDependenciesFile(dependenciesFile,lockFile)
                let remoteFileChangesPerGroup =
                    remoteFileChanges
                    |> Seq.groupBy fst
                    |> Map.ofSeq

                let hasNuGetChanges groupName =
                    match nuGetChangesPerGroup |> Map.tryFind groupName with
                    | None -> false
                    | Some x -> Seq.isEmpty x |> not

                let hasRemoteFileChanges groupName =
                    match remoteFileChangesPerGroup |> Map.tryFind groupName with
                    | None -> false
                    | Some x -> Seq.isEmpty x |> not

                let hasChangedSettings groupName =
                    match dependenciesFile.Groups |> Map.tryFind groupName with
                    | None -> true
                    | Some dependenciesFileGroup -> 
                        match lockFile.Groups |> Map.tryFind groupName with
                        | None -> true
                        | Some lockFileGroup -> dependenciesFileGroup.Options <> lockFileGroup.Options

                let hasChanges groupName _ = 
                    let hasChanges = hasChangedSettings groupName || hasNuGetChanges groupName || hasRemoteFileChanges groupName
                    if not hasChanges then
                        tracefn "Skipping resolver for group %O since it is already up-to-date" groupName
                    hasChanges

                let groups =
                    dependenciesFile.Groups
                    |> Map.filter hasChanges

                nuGetChanges,groups

        let preferredVersions = 
            DependencyChangeDetection.GetPreferredNuGetVersions lockFile
            |> getPreferredVersionsF
        preferredVersions changes,groups

    let resolution = dependenciesFile.Resolve(force, getSha1, getVersionsF, getPackageDetailsF, groupsToUpdate, updateMode)

    let groups = 
        dependenciesFile.Groups
        |> Map.map (fun groupName dependenciesGroup -> 
                match resolution |> Map.tryFind groupName with
                | Some group ->
                    let model = group.ResolvedPackages.GetModelOrFail()
                    for x in model do
                        if x.Value.Unlisted then
                            traceWarnfn "The owner of %O %A has unlisted the package. This could mean that the package version is deprecated or shouldn't be used anymore." x.Value.Name x.Value.Version

                    { Name = dependenciesGroup.Name
                      Options = dependenciesGroup.Options
                      Resolution = model
                      RemoteFiles = group.ResolvedSourceFiles }
                | None -> lockFile.GetGroup groupName) // just copy from lockfile
    
    LockFile(lockFile.FileName, groups)

let SelectiveUpdate(dependenciesFile : DependenciesFile, updateMode, semVerUpdateMode, force) =
    let lockFileName = DependenciesFile.FindLockfile dependenciesFile.FileName
    let oldLockFile,updateMode =
        if not lockFileName.Exists then
            LockFile.Parse(lockFileName.FullName, [||]),UpdateAll // Change updateMode to UpdateAll
        else
            LockFile.LoadFrom lockFileName.FullName,updateMode

    let getSha1 origin owner repo branch auth = RemoteDownload.getSHA1OfBranch origin owner repo branch auth |> Async.RunSynchronously
    let root = Path.GetDirectoryName dependenciesFile.FileName
    let inline getVersionsF sources resolverStrategy groupName packageName = 
        let versions = NuGetV2.GetVersions root (sources, packageName)
        match resolverStrategy with
        | ResolverStrategy.Max -> List.sortDescending versions
        | ResolverStrategy.Min -> List.sort versions

    let lockFile = 
        selectiveUpdate
            force 
            getSha1
            getVersionsF
            (NuGetV2.GetPackageDetails root force)
            oldLockFile 
            dependenciesFile 
            updateMode
            semVerUpdateMode
    lockFile.Save()
    lockFile

/// Smart install command
let SmartInstall(dependenciesFile, updateMode, options : UpdaterOptions) =
    let lockFile = SelectiveUpdate(dependenciesFile, updateMode, options.Common.SemVerUpdateMode, options.Common.Force)

    let root = Path.GetDirectoryName dependenciesFile.FileName
    let projects = InstallProcess.findAllReferencesFiles root |> returnOrFail

    if not options.NoInstall then
        InstallProcess.InstallIntoProjects(options.Common, dependenciesFile, lockFile, projects)

/// Update a single package command
let UpdatePackage(dependenciesFileName, groupName, packageName : PackageName, newVersion, options : UpdaterOptions) =
    let dependenciesFile = DependenciesFile.ReadFromFile(dependenciesFileName)

    if not <| dependenciesFile.HasPackage(groupName, packageName) then
        failwithf "Package %O was not found in paket.dependencies in group %O." packageName groupName

    let dependenciesFile =
        match newVersion with
        | Some v -> dependenciesFile.UpdatePackageVersion(groupName,packageName, v)
        | None -> 
            tracefn "Updating %O in %s group %O" packageName dependenciesFileName groupName
            dependenciesFile

    let filter = PackageFilter.ofName packageName

    SmartInstall(dependenciesFile, UpdateFiltered(groupName,filter), options)

/// Update a filtered list of packages
let UpdateFilteredPackages(dependenciesFileName, groupName, packageName : PackageName, newVersion, options : UpdaterOptions) =
    let dependenciesFile = DependenciesFile.ReadFromFile(dependenciesFileName)

    let filter = PackageFilter <| packageName.ToString()

    let dependenciesFile =
        match newVersion with
        | Some v -> dependenciesFile.UpdatePackageVersion(groupName,packageName, v)
        | None -> 
            tracefn "Updating %O in %s group %O" packageName dependenciesFileName groupName
            dependenciesFile

    SmartInstall(dependenciesFile, UpdateFiltered(groupName, filter), options)

/// Update a single group command
let UpdateGroup(dependenciesFileName, groupName,  options : UpdaterOptions) =
    let dependenciesFile = DependenciesFile.ReadFromFile(dependenciesFileName)

    if not <| dependenciesFile.Groups.ContainsKey groupName then

        failwithf "Group %O was not found in paket.dependencies." groupName
    tracefn "Updating group %O in %s" groupName dependenciesFileName

    SmartInstall(dependenciesFile, UpdateGroup groupName, options)

/// Update command
let Update(dependenciesFileName, options : UpdaterOptions) =
    let dependenciesFile = DependenciesFile.ReadFromFile(dependenciesFileName)
    
    SmartInstall(dependenciesFile, UpdateAll, options)
