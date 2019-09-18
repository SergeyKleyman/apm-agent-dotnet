using System;
using System.Text;
using System.Web.Hosting;
using Elastic.Apm.Config;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;

namespace Elastic.Apm.AspNetFullFramework
{
	internal class FullFrameworkConfigReader : EnvironmentConfigurationReader
	{
		private const string ThisClassName = nameof(FullFrameworkConfigReader);

		private new readonly IApmLogger _logger;

		public FullFrameworkConfigReader(IApmLogger logger = null) : base(logger) => _logger = logger?.Scoped(ThisClassName);

		protected override string DiscoverServiceName() => DiscoverFullFrameworkServiceName() ?? base.DiscoverServiceName();

		private string DiscoverFullFrameworkServiceName()
		{
			var retVal = new StringBuilder();
			AppendIfNotNull(FindSiteName());
			AppendIfNotNull(FindAppPoolName());
			return retVal.ToString();

			void AppendIfNotNull(string nameToAppend)
			{
				const string separator = "_";
				if (nameToAppend != null) retVal.AppendSeparatedIfNotEmpty(separator, nameToAppend);
			}
		}

		private string FindAppPoolName()
		{
			try
			{
				return System.Environment.GetEnvironmentVariable("APP_POOL_ID");
			}
			catch (Exception ex)
			{
				_logger.Error()?.Log("Failed to get app pool name: {Exception}", ex);
				return null;
			}
		}

		private string FindSiteName()
		{
			try
			{
				return HostingEnvironment.SiteName;
			}
			catch (Exception ex)
			{
				_logger.Error()?.Log("Failed to get site name: {Exception}", ex);
				return null;
			}
		}
	}
}
