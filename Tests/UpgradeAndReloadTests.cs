using System.Xml.Linq;
using Business;
using Microsoft.Extensions.Logging.Abstractions;
using Models;
using Web.Custom;

namespace Tests
{
    public class FormReloaderTests
    {
        private const string Form = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="List">
              <fields name="master"><field name="a" fastName="a"/></fields>
              <query><text>select 1</text></query>
            </import>
            """;

        [Fact]
        public async Task FileDoi_NapLaiCatalog()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-reload-").FullName;
            try
            {
                ImportFormCatalog Load() => ImportFormCatalog.LoadDirectory(dir);
                var runtime = new FormRuntime(Load(), null);
                using var reloader = new FormReloader(runtime, Load, dir, NullLogger<FormReloader>.Instance) { Debounce = TimeSpan.FromMilliseconds(50) };
                await reloader.StartAsync(CancellationToken.None);

                Assert.Equal(RouteMatch.NotFound, runtime.Catalog.Resolve("POST", "/api/customer/get", out _));

                File.WriteAllText(Path.Combine(dir, "getCustomer.xml"), Form);
                Assert.True(await WaitUntil(() => runtime.Catalog.Resolve("POST", "/api/customer/get", out _) == RouteMatch.Found), "thêm file → có route");

                File.Delete(Path.Combine(dir, "getCustomer.xml"));
                Assert.True(await WaitUntil(() => runtime.Catalog.Resolve("POST", "/api/customer/get", out _) == RouteMatch.NotFound), "xoá file → mất route");

                await reloader.StopAsync(CancellationToken.None);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task NapLaiLoi_GiuCatalogCu()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-reload-").FullName;
            try
            {
                File.WriteAllText(Path.Combine(dir, "getCustomer.xml"), Form);
                ImportFormCatalog initial = ImportFormCatalog.LoadDirectory(dir);
                var runtime = new FormRuntime(initial, null);
                int calls = 0;
                ImportFormCatalog Failing()
                {
                    calls++;
                    throw new InvalidOperationException("mất kết nối SQL");
                }

                using var reloader = new FormReloader(runtime, Failing, dir, NullLogger<FormReloader>.Instance) { Debounce = TimeSpan.FromMilliseconds(50) };
                await reloader.StartAsync(CancellationToken.None);

                File.WriteAllText(Path.Combine(dir, "setOther.xml"), Form);
                Assert.True(await WaitUntil(() => calls > 0));

                Assert.Same(initial, runtime.Catalog);
                await reloader.StopAsync(CancellationToken.None);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static async Task<bool> WaitUntil(Func<bool> condition)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition())
                {
                    return true;
                }
                await Task.Delay(50);
            }
            return condition();
        }
    }
}
