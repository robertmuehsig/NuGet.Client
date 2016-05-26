﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGet.PackageManagement.UI
{
    /// <summary>
    /// Implements a consolidated metadata provider for multiple package sources 
    /// with optional local repository as a fallback metadata source.
    /// </summary>
    internal class MultiSourcePackageMetadataProvider : IPackageMetadataProvider
    {
        private readonly IEnumerable<SourceRepository> _sourceRepositories;
        private readonly SourceRepository _localRepository;
        private readonly SourceRepository _globalLocalRepository;
        private readonly Common.ILogger _logger;
        private readonly NuGetProject[] _projects;
        private readonly bool _isSolution;
        private IEnumerable<Packaging.PackageReference> _packageReferences;

        public MultiSourcePackageMetadataProvider(
            IEnumerable<SourceRepository> sourceRepositories,
            SourceRepository optionalLocalRepository,
            SourceRepository optionalGlobalLocalRepository,
            NuGetProject[] projects,
            bool isSolution,
            Common.ILogger logger)
        {
            if (sourceRepositories == null)
            {
                throw new ArgumentNullException(nameof(sourceRepositories));
            }

            if (projects == null)
            {
                throw new ArgumentNullException(nameof(projects));
            }
            _sourceRepositories = sourceRepositories;

            _localRepository = optionalLocalRepository;

            _globalLocalRepository = optionalGlobalLocalRepository;

            _projects = projects;

            _isSolution = isSolution;

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }
            _logger = logger;
        }

        public async Task<IPackageSearchMetadata> GetPackageMetadataAsync(PackageIdentity identity, bool includePrerelease, CancellationToken cancellationToken)
        {
            var tasks = _sourceRepositories
                .Select(r => r.GetPackageMetadataAsync(identity, includePrerelease, cancellationToken))
                .ToList();

            if (_localRepository != null)
            {
                tasks.Add(_localRepository.GetPackageMetadataFromLocalSourceAsync(identity, cancellationToken));
            }

            var ignored = tasks
                .Select(task => task.ContinueWith(LogError, TaskContinuationOptions.OnlyOnFaulted))
                .ToArray();

            var completed = (await Task.WhenAll(tasks))
                .Where(m => m != null);

            var master = completed.FirstOrDefault(m => !string.IsNullOrEmpty(m.Summary))
                ?? completed.FirstOrDefault()
                ?? PackageSearchMetadataBuilder.FromIdentity(identity).Build();

            return master.WithVersions(
                asyncValueFactory: () => MergeVersionsAsync(identity, completed));
        }

        public async Task<IPackageSearchMetadata> GetLatestPackageMetadataAsync(PackageIdentity identity,
            bool includePrerelease, CancellationToken cancellationToken)
        {
            VersionRange allowedVersions = null;

            // at this time, allowed version range should be applied at project level. 
            // For solution level, we will apply allowed version range for all the selected projects in detail control model
            if (!_isSolution)
            {
                if (_packageReferences == null)
                {
                    // get all package references for all the projects and cache locally
                    var packageReferencesTasks = _projects.Select(project => project.GetInstalledPackagesAsync(cancellationToken));
                    _packageReferences = (await Task.WhenAll(packageReferencesTasks)).SelectMany(p => p).Where(p => p != null);
                }

                // filter package references for current package identity
                var matchedPackageReferences = _packageReferences.Where(r => StringComparer.OrdinalIgnoreCase.Equals(r.PackageIdentity.Id, identity.Id));

                // Allowed version range for current package across all selected projects
                var allowedVersionsRange = matchedPackageReferences.Select(r => r.AllowedVersions).Where(v => v != null);

                if (allowedVersionsRange.Any())
                {
                    // Find a common range which satisfies all the version ranges across projects
                    allowedVersions = VersionRange.FindCommonRange(allowedVersionsRange);
                }
            }

            var tasks = _sourceRepositories
                .Select(r => r.GetLatestPackageMetadataAsync(identity.Id, includePrerelease, cancellationToken, allowedVersions))
                .ToArray();

            var ignored = tasks
                .Select(task => task.ContinueWith(LogError, TaskContinuationOptions.OnlyOnFaulted))
                .ToArray();

            var completed = (await Task.WhenAll(tasks))
                .Where(m => m != null);

            var highest = completed
                .OrderByDescending(e => e.Identity.Version, VersionComparer.VersionRelease)
                .FirstOrDefault();

            return highest?.WithVersions(
                asyncValueFactory: () => MergeVersionsAsync(identity, completed));
        }

        public async Task<IEnumerable<IPackageSearchMetadata>> GetPackageMetadataListAsync(string packageId, bool includePrerelease, bool includeUnlisted, CancellationToken cancellationToken)
        {
            var tasks = _sourceRepositories
                .Select(r => r.GetPackageMetadataListAsync(packageId, includePrerelease, includeUnlisted, cancellationToken))
                .ToArray();

            var ignored = tasks
                .Select(task => task.ContinueWith(LogError, TaskContinuationOptions.OnlyOnFaulted))
                .ToArray();

            var completed = (await Task.WhenAll(tasks))
                .Where(m => m != null);

            return completed.SelectMany(p => p);
        }

        public async Task<IPackageSearchMetadata> GetLocalPackageMetadataAsync(PackageIdentity identity, bool includePrerelease, CancellationToken cancellationToken)
        {
            var result = await _localRepository.GetPackageMetadataFromLocalSourceAsync(identity, cancellationToken);

            if (result == null)
            {
                result = await _globalLocalRepository?.GetPackageMetadataFromLocalSourceAsync(identity, cancellationToken);

                if (result == null)
                {
                    return null;
                }
            }

            return result.WithVersions(asyncValueFactory: () => FetchAndMergeVersionsAsync(identity, includePrerelease, cancellationToken));
        }

        private static async Task<IEnumerable<VersionInfo>> MergeVersionsAsync(PackageIdentity identity, IEnumerable<IPackageSearchMetadata> packages)
        {
            var versions = await Task.WhenAll(packages.Select(m => m.GetVersionsAsync()));

            var allVersions = versions
                .SelectMany(v => v)
                .Concat(new[] { new VersionInfo(identity.Version) });

            return allVersions
                .GroupBy(v => v.Version)
                .Select(g => g.OrderBy(v => v.DownloadCount).First())
                .ToArray();
        }

        private async Task<IEnumerable<VersionInfo>> FetchAndMergeVersionsAsync(PackageIdentity identity, bool includePrerelease, CancellationToken cancellationToken)
        {
            var tasks = _sourceRepositories
                .Select(r => r.GetPackageMetadataAsync(identity, includePrerelease, cancellationToken))
                .ToList();

            if (_localRepository != null)
            {
                tasks.Add(_localRepository.GetPackageMetadataFromLocalSourceAsync(identity, cancellationToken));
            }

            var ignored = tasks
                .Select(task => task.ContinueWith(LogError, TaskContinuationOptions.OnlyOnFaulted))
                .ToArray();

            var completed = (await Task.WhenAll(tasks))
                .Where(m => m != null);

            return await MergeVersionsAsync(identity, completed);
        }

        private void LogError(Task task)
        {
            foreach (var ex in task.Exception.Flatten().InnerExceptions)
            {
                _logger.LogError(ex.ToString());
            }
        }

    }
}
