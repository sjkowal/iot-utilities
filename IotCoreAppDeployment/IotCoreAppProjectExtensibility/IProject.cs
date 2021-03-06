// Copyright (c) Microsoft. All rights reserved.

using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.Iot.IotCoreAppProjectExtensibility
{
    public interface IProject
    {
        string Name { get; }
        string IdentityName { get; }

        string SourceInput { set; get; }


        TargetPlatform ProcessorArchitecture { set; get; }
        SdkVersion SdkVersion { set; get; }
        DependencyConfiguration DependencyConfiguration { set; get; }

        bool IsSourceSupported(string source);
        IBaseProjectTypes GetBaseProjectType();

        ReadOnlyCollection<IContentChange> GetCapabilities();
        ReadOnlyCollection<IContentChange> GetAppxContentChanges();
        bool GetAppxMapContents(Collection<string> resourceMetadata, Collection<string> files, string outputFolder);
        ReadOnlyCollection<FileStreamInfo> GetAppxContents();
        ReadOnlyCollection<FileStreamInfo> GetDependencies(Collection<IDependencyProvider> availableDependencyProviders);
    }

    public interface IProjectWithCustomBuild : IProject
    {
        Task<bool> BuildAsync(string outputFolder, StreamWriter logging);
    }
}
