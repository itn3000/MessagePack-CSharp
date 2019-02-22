using System;
using StLogger = Microsoft.Build.Logging.StructuredLogger;

namespace MessagePack.CodeGenerator
{
    internal static class BuildLogUtil
    {
        public static IEnumerable<StLogger.Error> GetErrorRecursive(StLogger.Build build)
        {
            var lst = new List<StLogger.Error>();
            build.VisitAllChildren<StLogger.Error>(er => lst.Add(er));
            return lst;
        }
    }

}