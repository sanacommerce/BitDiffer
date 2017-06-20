using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using BitDiffer.Common.Exceptions;
using BitDiffer.Common.Model;
using BitDiffer.Common.Utility;
using BitDiffer.Common.Misc;
using BitDiffer.Common.Configuration;
using System.Runtime.InteropServices.WindowsRuntime;

namespace BitDiffer.Extractor
{
	public class AssemblyExtractor : MarshalByRefObject
	{
		private DiffConfig _config;
		private string _assemblyFile;

		static AssemblyExtractor()
		{
			if (AppDomain.CurrentDomain.FriendlyName.StartsWith(Constants.ExtractionDomainPrefix))
			{
				AppDomain.CurrentDomain.AssemblyResolve += domain_AssemblyResolve;
			}
		}

		public AssemblyDetail ExtractFrom(string assemblyFile, DiffConfig config)
		{
            
			Assembly assembly;

			_assemblyFile = assemblyFile;
			_config = config;

            // Handle standard and .winmd resolve events
			AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += CurrentDomain_ReflectionOnlyAssemblyResolve;
            WindowsRuntimeMetadata.ReflectionOnlyNamespaceResolve += WindowsRuntimeMetadata_ReflectionOnlyNamespaceResolve;

            try
		    {
		        if (config.UseReflectionOnlyContext)
		        {
		            Log.Info("Loading assembly {0} (ReflectionContext)", assemblyFile);
		            assembly = Assembly.ReflectionOnlyLoadFrom(assemblyFile);
		        }
		        else
		        {
		            Log.Info("Loading assembly {0}", assemblyFile);
		            assembly = Assembly.LoadFrom(assemblyFile);
		        }

		        return new AssemblyDetail(assembly);
		    }
            catch (Exception ex)
            {
                var errMessage = ex.GetNestedExceptionMessage();
                Log.Error(errMessage);
                throw new Exception(errMessage, ex);
		    }
			finally
			{
				AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= CurrentDomain_ReflectionOnlyAssemblyResolve;
                WindowsRuntimeMetadata.ReflectionOnlyNamespaceResolve -= WindowsRuntimeMetadata_ReflectionOnlyNamespaceResolve;
            }
		}

        private void WindowsRuntimeMetadata_ReflectionOnlyNamespaceResolve(object sender, NamespaceResolveEventArgs e)
        {
            // Create a directory list of paths to use when resolving assemblies
            List<string> dirs = new List<string>();

            // Add the path to the current assembly as one possible location
            dirs.Add(Path.GetDirectoryName(_assemblyFile));

            // Add non-empty reference directories as well
            if (_config.ReferenceDirectories != null)
            {
                string[] splitDirs = _config.ReferenceDirectories.Split(';');
                foreach (var d in splitDirs)
                    if (!string.IsNullOrEmpty(d))
                        dirs.Add(d);
            }

            // Resolve the namespace using the directory list. Unfortunately the system root usually overrides the passed folder.
            Log.Verbose("Resolving namespace '{0}' in assemblies located at '{1}'", e.NamespaceName, string.Join(";", dirs));
            IEnumerable<string> foundAssemblies = WindowsRuntimeMetadata.ResolveNamespace(e.NamespaceName, dirs);

            foreach (var assemblyName in foundAssemblies)
                Log.Verbose(@"Found namespace '{0}' in assembly '{1}'", e.NamespaceName, assemblyName);

            string path = foundAssemblies.FirstOrDefault();
            if (path == null)
                return;

            // HACK: Because the system path will override any local paths during resolution, go ahead and add
            // all .winmd files discovered.
            foreach (var assemblyPath in dirs)
            {
                var localAssemblies = Directory.GetFiles(assemblyPath, "*.winmd");
                foreach (var localAssembly in localAssemblies)
                    if (localAssembly != e.RequestingAssembly.Location)
                        e.ResolvedAssemblies.Add(Assembly.ReflectionOnlyLoadFrom(localAssembly));
            }

            // Finally add the path that was originally found in the earlier lookup.
            var assembly = Assembly.ReflectionOnlyLoadFrom(path);
            if (!e.ResolvedAssemblies.Contains(assembly))
                e.ResolvedAssemblies.Add(assembly);
        }

        private Assembly CurrentDomain_ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            Assembly assembly = null;

            Log.Verbose("Attempting to resolve assembly reference '{0}'", args.Name);

            Assembly[] list = AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies();
            foreach (Assembly asm in list)
            {
                if (asm.FullName == args.Name)
                {
                    return asm;
                }
            }

            if (_config.ReferenceDirectories != null)
                assembly = LoadAssemblyFromDirectories(args, _config.ReferenceDirectories.Split(';'));

            if (assembly == null)
            {
                if (_config.TryResolveGACFirst)
                {
                    assembly = LoadAssemblyFromGAC(args.Name);

                    if (assembly == null)
                        assembly = LoadAssemblyFromFile(args.Name);
                }
                else
                {
                    assembly = LoadAssemblyFromFile(args.Name);

                    if (assembly == null)
                        assembly = LoadAssemblyFromGAC(args.Name);
                }
            }

            if (assembly == null)
                Log.Error("Could not resolve assembly reference '{0}'.", args.Name);
            else
                Log.Verbose("Assembly loaded.");

            return assembly;
        }

        private Assembly LoadAssemblyFromDirectories(ResolveEventArgs args, string[] dirs)
        {
            // Look for the specific version of dll, to not suffer from breaking changes in reference dlls.
            foreach (string dir in dirs)
            {
                var assembly = LoadExactAssemblyFromFile(args.Name, dir);
                if (assembly != null)
                    return assembly;
            }

            // If specific version not found, return the first avaliable.
            foreach (string dir in dirs)
            {
                var assembly = LoadAssemblyFromFile(args.Name, dir);
                if (assembly != null)
                    return assembly;
            }

            return null;
        }

        private static Assembly domain_AssemblyResolve(object sender, ResolveEventArgs args)
		{
			// Apparently a bug in .NET... it has trouble resolving assemblies that are already loaded!
			Assembly[] list = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly asm in list)
			{
				if (asm.FullName == args.Name)
				{
					Log.Verbose("Resolving assembly {0} that is already loaded", asm.FullName);
					return asm;
				}
			}

      //If the Assembly we're trying to resolve cannot ever be resolved (because the Assembly in question is missing from GAC 
      //and is not included locally, for example) Assembly.Load will cause a recursive loop resulting in a StackOverflowException.
      //To avoid this we need to unregister this event handler from the AssemblyResolve event whilst we attempt to 
      //load this assembly. 
      //MSDN actually has an example of how we should not Assembly.Load inside this event.
      //see "What the Event Handler Should Not Do" @ https://msdn.microsoft.com/en-us/library/ff527268%28v=vs.110%29.aspx
      AppDomain.CurrentDomain.AssemblyResolve -= domain_AssemblyResolve;
      var loadedAsm = Assembly.Load(args.Name);
      AppDomain.CurrentDomain.AssemblyResolve += domain_AssemblyResolve;
      return loadedAsm;

		}

		private Assembly LoadAssemblyFromGAC(string name)
		{
			Log.Verbose("Searching for assembly '{0}' in GAC", name);

			try
			{
				Assembly assembly = Assembly.ReflectionOnlyLoad(name);
				if (assembly != null)
				{
					Log.Verbose("Resolving assembly {0} from GAC", name);
					return assembly;
				}
			}
			catch
			{
			}

			return null;
		}

        private Assembly LoadAssemblyFromFile(string name)
        {
            return LoadAssemblyFromFile(name, Path.GetDirectoryName(_assemblyFile));
        }

        private Assembly LoadExactAssemblyFromFile(string fullAssemblyName, string dir)
        {
            string name;
            var filePath = GetAssaemblyPath(fullAssemblyName, dir, out name);
            var parts = fullAssemblyName.Split(',');
            string version = parts[1].Split('=').Last();

            Log.Verbose("Searching for assembly file '{0}'", filePath);

            if (File.Exists(filePath))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                if (versionInfo.ProductVersion.Equals(version, StringComparison.InvariantCultureIgnoreCase))
                    return LoadAssembly(filePath, name);
            }

            return null;
        }

        private Assembly LoadAssemblyFromFile(string fullAssemblyName, string dir)
        {
            string name;
            var filePath = GetAssaemblyPath(fullAssemblyName, dir, out name);

            Log.Verbose("Searching for assembly file '{0}'", filePath);

            if (File.Exists(filePath))
                return LoadAssembly(filePath, name);

            return null;
        }

        private Assembly LoadAssembly(string filePath, string name)
        {
            Log.Verbose("Resolving assembly {0} from file", name);
            return Assembly.ReflectionOnlyLoadFrom(filePath);
        }

        private string GetAssaemblyPath(string fullAssemblyName, string dir, out string name)
        {
            name = fullAssemblyName;
            if (name.IndexOf(',') > 0)
                name = name.Substring(0, name.IndexOf(','));

            string filePath = Path.Combine(dir, name);

            if (!filePath.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase))
                filePath += ".dll";

            return filePath;
        }

        public void AddTraceListener(TraceListener listener)
		{
			Trace.Listeners.Add(listener);
		}
	}
}
