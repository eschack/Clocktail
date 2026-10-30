using Clocktail.Domain.Platform;

namespace Clocktail.Tests.Domain;

public class BrokerInstallBundleTests
{
    [Fact]
    public void A_single_file_broker_installs_its_proof_helper_but_not_the_agent_server()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ct-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string file in new[] { "Clocktail.Broker.exe", "clocktail-lab.exe", "clocktail-mcp.exe", "Clocktail.Cli.exe" })
                File.WriteAllText(Path.Combine(dir, file), "offline fixture");
            var files = BrokerInstallBundle.Files(dir, Path.Combine(dir, "Clocktail.Broker.exe"), singleFile: true);
            Assert.Equal(new[] { "Clocktail.Broker.exe", "clocktail-lab.exe" }.Order(), files.Select(Path.GetFileName).Order());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_helper_is_rejected_before_the_installation_can_stop_the_old_task()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ct-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string broker = Path.Combine(dir, "Clocktail.Broker.exe");
            File.WriteAllText(broker, "offline fixture");
            Assert.Throws<FileNotFoundException>(() => BrokerInstallBundle.Files(dir, broker, singleFile: true));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Framework_dependent_installation_keeps_dependencies_without_duplicating_the_helper()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ct-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string file in new[] { "Clocktail.Broker.exe", "clocktail-lab.exe", "Clocktail.Domain.dll", "Clocktail.Broker.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(dir, file), "offline fixture");
            var files = BrokerInstallBundle.Files(dir, Path.Combine(dir, "Clocktail.Broker.exe"), singleFile: false);
            Assert.Equal(4, files.Count);
            Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally { Directory.Delete(dir, true); }
    }
}
