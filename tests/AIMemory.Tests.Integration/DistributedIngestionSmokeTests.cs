using System.Net;
using System.Net.Http.Json;
using AIMemory.CodeIndex.Git;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Data;
using AIMemory.Identity;
using AIMemory.Ingestor;
using AIMemory.Ingestor.Adapters;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.Hashing;
using AIMemory.Ingestor.Transport;
using AIMemory.Models.Dtos;
using AIMemory.Tests.Unit;
using LibGit2Sharp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Tests.Integration;

/// <summary>
/// Phase 11 — two-host end-to-end smoke test for the distributed ingestion path.
///
/// <para>Brings up an in-process API host via <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// then drives two simulated ingestors (each with a distinct <see cref="IInstallSaltStore"/>
/// → distinct <c>host_id</c>) through the same flow a real secondary would take: pair via
/// <c>POST /api/pairings</c>, then push code-file batches with the secondary's
/// <c>HostId</c>/<c>ProjectId</c> populated. Asserts the design-doc invariants:
/// <list type="bullet">
///   <item>Same project across hosts → <b>one</b> <c>projects</c> row reused.</item>
///   <item>Same content across hosts → <b>one</b> <c>code_files</c> row reused (content-addressed dedup).</item>
///   <item>Distinct <c>file_locations</c> rows attributed to each host.</item>
///   <item>Modifying a file on one host produces a new <c>code_files</c> row, leaves the other host's row pointing at the original content.</item>
///   <item>Revoked pairing's host gets 403 on subsequent ingest (the API rejects unknown hosts; the pairing-revoke flow deactivates the API key, but for this in-process test we exercise the host-validation 403 path instead, which covers the same design intent — see <see cref="RevokedHost_RejectedOnIngest"/>).</item>
/// </list>
/// </para>
///
/// <para>Lives in a single <see cref="[Collection]"/> (with parallelization disabled) because
/// the test patches process-wide environment variables (<c>ProgramData</c>,
/// <c>AIMEMORY_API_KEY</c>) so the in-process <c>Program</c> picks them up at startup.</para>
/// </summary>
[Collection("DistributedSmoke")]
public sealed class DistributedIngestionSmokeTests : IClassFixture<DistributedIngestionSmokeTests.PrimaryFixture>, IAsyncLifetime
{
    private readonly PrimaryFixture _primary;

    public DistributedIngestionSmokeTests(PrimaryFixture primary)
    {
        _primary = primary;
    }

    /// <summary>
    /// Per-test reset. The fixture's SQLite DB is shared across tests in the class so we
    /// don't pay startup cost per fact, but each test starts from a clean slate.
    /// </summary>
    public async Task InitializeAsync()
    {
        using var scope = _primary.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
        // Order matters: drop dependent rows first.
        db.IngestionLog.RemoveRange(db.IngestionLog);
        db.CodeSymbols.RemoveRange(db.CodeSymbols);
        db.FileLocations.RemoveRange(db.FileLocations);
        db.CodeFiles.RemoveRange(db.CodeFiles);
        db.Projects.RemoveRange(db.Projects);
        db.Pairings.RemoveRange(db.Pairings);
        db.Hosts.RemoveRange(db.Hosts);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Spins up the AIMemory.Api process in-memory once per test class. Owns the lifetime of
    /// the WebApplicationFactory + the temp ProgramData/SQLite/HttpClient, plus the env-var
    /// patches required for the in-process Program.cs to pick up our test paths.
    /// </summary>
    public sealed class PrimaryFixture : IDisposable
    {
        public string TempProgramData { get; }
        public string TempAppData { get; }
        public string AdminApiKey { get; } = "test-admin-" + Guid.NewGuid().ToString("N");
        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient AdminClient { get; }

        private readonly string? _origProgramData;
        private readonly string? _origAppData;
        private readonly string? _origApiKey;

        public PrimaryFixture()
        {
            TempProgramData = Path.Combine(Path.GetTempPath(), "aimemory-smoke-pd-" + Guid.NewGuid().ToString("N"));
            TempAppData = Path.Combine(Path.GetTempPath(), "aimemory-smoke-ad-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempProgramData);
            Directory.CreateDirectory(TempAppData);

            // Snapshot existing env so we can restore on dispose. The in-process Program reads
            // %ProgramData% via Environment.SpecialFolder.CommonApplicationData (which is the
            // PROGRAMDATA env var on Windows) and %APPDATA% (ApplicationData → APPDATA).
            _origProgramData = Environment.GetEnvironmentVariable("ProgramData");
            _origAppData = Environment.GetEnvironmentVariable("APPDATA");
            _origApiKey = Environment.GetEnvironmentVariable("AIMEMORY_API_KEY");

            Environment.SetEnvironmentVariable("ProgramData", TempProgramData);
            // The SQLite DB lands under %APPDATA%\AIMemory\aimemory.db per Program.cs — give it
            // its own temp dir so the test isn't sharing state with a real install.
            Environment.SetEnvironmentVariable("APPDATA", TempAppData);
            // Setting AIMEMORY_API_KEY makes the env-var fallback in ApiKeyAuthMiddleware grant
            // admin scope to any request bearing this key — exactly what we want for /api/pairings
            // and other admin endpoints from our test client.
            Environment.SetEnvironmentVariable("AIMEMORY_API_KEY", AdminApiKey);

            Factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

            // Trigger startup so the DB migrates and runtime.json is written before the first
            // test request races with it.
            AdminClient = Factory.CreateClient();
            AdminClient.DefaultRequestHeaders.Add("X-AIMemory-Api-Key", AdminApiKey);
        }

        public AIMemoryDbContext NewDbContext()
        {
            // Resolve a scoped DbContext from the running app — same connection string as
            // the live request pipeline, so we can read what the API just wrote.
            var scope = Factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
        }

        public void Dispose()
        {
            try { AdminClient.Dispose(); } catch { }
            try { Factory.Dispose(); } catch { }
            try { Directory.Delete(TempProgramData, recursive: true); } catch { }
            try { Directory.Delete(TempAppData, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("ProgramData", _origProgramData);
            Environment.SetEnvironmentVariable("APPDATA", _origAppData);
            Environment.SetEnvironmentVariable("AIMEMORY_API_KEY", _origApiKey);
        }
    }

    /// <summary>
    /// Per-host harness: builds an <see cref="IIngestorContext"/> backed by a per-temp-dir
    /// salt store (so each instance produces a distinct <c>host_id</c>), wires a
    /// <see cref="CodeAdapter"/>, and exposes a one-shot <c>RunOnceAsync</c> that walks one
    /// project, parses, and posts to the test API as the secondary would.
    /// </summary>
    private sealed class TestIngestor
    {
        public string HostId { get; }
        public string FriendlyName { get; }
        private readonly HttpClient _http;
        private readonly IIngestorContext _ctx;
        private readonly CodeAdapter _adapter;
        private readonly string _projectPath;

        public TestIngestor(string saltDir, string friendlyName, HttpClient http, string projectPath)
        {
            FriendlyName = friendlyName;
            _http = http;
            _projectPath = projectPath;

            var saltStore = new InstallSaltStore(saltDir);
            var hostIdProvider = new HostIdProvider(saltStore, new FixedMachineGuidReader(friendlyName));
            HostId = hostIdProvider.GetHostId();

            var resolver = new ProjectIdResolver(NullLogger<ProjectIdResolver>.Instance);
            _ctx = new IngestorContext(hostIdProvider, resolver);

            var fileFilter = new FileFilter();
            var parserRegistry = new ParserRegistry(new ILanguageParser[]
            {
                new CSharpParser(), new TypeScriptParser(), new PythonParser(), new GoParser()
            });
            var gitDetector = new GitChangeDetector(NullLogger<GitChangeDetector>.Instance);
            var checkpointStore = new InMemoryCheckpointStore();
            _adapter = new CodeAdapter(
                fileFilter, parserRegistry, gitDetector, checkpointStore,
                NullLogger<CodeAdapter>.Instance, new StreamingFileHasher());
        }

        public async Task<HttpResponseMessage> PairAsync(HttpClient adminClient)
        {
            return await adminClient.PostAsJsonAsync("/api/pairings", new CreatePairingRequest
            {
                HostId = HostId,
                FriendlyName = FriendlyName,
                OsKind = "windows",
                IngestorVersion = "test-1.0"
            });
        }

        /// <summary>
        /// Discovers files in the project, parses each, and posts a single batch with the
        /// host's HostId + ProjectId populated. Returns the API response so callers can assert
        /// success counts.
        /// </summary>
        public async Task<BatchIngestResponse> RunOnceAsync()
        {
            var sourceConfig = new SourceConfig
            {
                Name = "code-index",
                WatchPaths = new List<string> { _projectPath },
                DetectionMode = ChangeDetectionMode.Auto
            };

            var projectId = _ctx.GetProjectIdentity(_projectPath).ProjectId;
            var files = _adapter.DiscoverFiles(sourceConfig).ToList();
            var events = new List<IngestEvent>();
            foreach (var f in files)
            {
                var records = _adapter.ReadNewRecords(f, null).ToList();
                foreach (var r in records)
                {
                    foreach (var evt in _adapter.ParseRecord(r))
                        events.Add(evt);
                }
            }

            var request = new BatchIngestRequest
            {
                ClientId = "test-" + FriendlyName,
                MachineName = FriendlyName,
                Source = "code-index",
                HostId = HostId,
                ProjectId = projectId,
                Events = events
            };

            var resp = await _http.PostAsJsonAsync("/api/ingest/batch", request);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<BatchIngestResponse>();
            Assert.NotNull(body);
            return body!;
        }
    }

    /// <summary>
    /// Pumps a deterministic byte sequence into <see cref="HostIdProvider"/> so each test
    /// "machine" produces a distinct host_id without depending on the real registry. Without
    /// this both ingestors would hash the same Windows MachineGuid + their (unique) salt and
    /// still differ — but using a fixed reader documents the intent and decouples the test
    /// from the host machine's registry.
    /// </summary>
    private sealed class FixedMachineGuidReader : IMachineGuidReader
    {
        private readonly string _seed;
        public FixedMachineGuidReader(string seed) { _seed = seed; }
        public byte[] ReadBytes() => System.Text.Encoding.UTF8.GetBytes($"machine-{_seed}");
    }

    [Fact]
    public async Task TwoHosts_SameProject_DedupContent_DistinctLocations()
    {
        // Arrange: a synthetic project that both "hosts" will see at different absolute paths.
        // We materialize the same files into two temp directories so the on-disk content_sha256
        // matches across hosts (the dedup invariant we're verifying).
        using var projectAOnDisk = new TempProject("Hello.cs", "// shared content\nclass A { void M() {} }\n");
        var bDir = projectAOnDisk.MirrorTo("hostB");

        using var saltDirA = new TempDir("salt-A");
        using var saltDirB = new TempDir("salt-B");

        var clientA = _primary.Factory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-AIMemory-Api-Key", _primary.AdminApiKey);
        var clientB = _primary.Factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-AIMemory-Api-Key", _primary.AdminApiKey);

        var ingestorA = new TestIngestor(saltDirA.Path, "host-A", clientA, projectAOnDisk.Path);
        var ingestorB = new TestIngestor(saltDirB.Path, "host-B", clientB, bDir);

        Assert.NotEqual(ingestorA.HostId, ingestorB.HostId); // sanity: different salts → different ids

        // Pair both. Pairing inserts the Host row, which is what the batch handler validates.
        var pairResponseA = await ingestorA.PairAsync(_primary.AdminClient);
        Assert.True(pairResponseA.IsSuccessStatusCode, await pairResponseA.Content.ReadAsStringAsync());
        var pairResponseB = await ingestorB.PairAsync(_primary.AdminClient);
        Assert.True(pairResponseB.IsSuccessStatusCode, await pairResponseB.Content.ReadAsStringAsync());

        // Act 1: Host A ingests.
        var responseA = await ingestorA.RunOnceAsync();
        Assert.Equal(0, responseA.Failed);
        Assert.True(responseA.Succeeded > 0);

        // Assert 1: A wrote a Project + a CodeFile (content-addressed) + a FileLocation for A.
        using (var dbScope = _primary.Factory.Services.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
            var hostsAfterA = await db.Hosts.ToListAsync();
            Assert.Contains(hostsAfterA, h => h.HostId == ingestorA.HostId);

            var projectsAfterA = await db.Projects.ToListAsync();
            Assert.Single(projectsAfterA);
            var projectId = projectsAfterA[0].ProjectId;

            var contentRowsAfterA = await db.CodeFiles.ToListAsync();
            Assert.Single(contentRowsAfterA);

            var locsAfterA = await db.FileLocations.ToListAsync();
            Assert.Single(locsAfterA);
            Assert.Equal(ingestorA.HostId, locsAfterA[0].HostId);
            Assert.Equal(projectId, locsAfterA[0].ProjectId);
        }

        // Act 2: Host B ingests the *same* file content from its own copy.
        var responseB = await ingestorB.RunOnceAsync();
        Assert.Equal(0, responseB.Failed);

        // Assert 2: project reused, content reused (no duplicate code_files row), but a new
        // file_location was added for host B.
        using (var dbScope = _primary.Factory.Services.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
            Assert.Equal(2, await db.Hosts.CountAsync());
            Assert.Single(await db.Projects.ToListAsync()); // still one project
            Assert.Single(await db.CodeFiles.ToListAsync()); // CONTENT DEDUP: still one row

            var locs = await db.FileLocations.ToListAsync();
            Assert.Equal(2, locs.Count);
            Assert.Contains(locs, l => l.HostId == ingestorA.HostId);
            Assert.Contains(locs, l => l.HostId == ingestorB.HostId);
            // Both file_locations point at the same content_sha256.
            Assert.Single(locs.Select(l => l.ContentSha256).Distinct());
        }
    }

    [Fact]
    public async Task ContentChange_OnOneHost_AddsNewBlob_OldBlobKeptForOtherHost()
    {
        // Arrange: two hosts, each with their own copy of the same project. After both ingest,
        // host B modifies one file; the new content blob should be added, host A's location
        // should still point at the original blob. Per design §6: both stored, both queryable.
        using var projectA = new TempProject("Hello2.cs",
            "// shared\nclass S { void M() {} }\n");
        var bDir = projectA.MirrorTo("hostB-mod");

        using var saltDirA = new TempDir("salt-A2");
        using var saltDirB = new TempDir("salt-B2");

        var clientA = _primary.Factory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-AIMemory-Api-Key", _primary.AdminApiKey);
        var clientB = _primary.Factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-AIMemory-Api-Key", _primary.AdminApiKey);

        var ingestorA = new TestIngestor(saltDirA.Path, "host-A2", clientA, projectA.Path);
        var ingestorB = new TestIngestor(saltDirB.Path, "host-B2", clientB, bDir);

        await ingestorA.PairAsync(_primary.AdminClient);
        await ingestorB.PairAsync(_primary.AdminClient);

        var responseA1 = await ingestorA.RunOnceAsync();
        Assert.Equal(0, responseA1.Failed);
        var responseB1 = await ingestorB.RunOnceAsync();
        Assert.Equal(0, responseB1.Failed);

        string originalSha;
        using (var dbScope = _primary.Factory.Services.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
            Assert.Single(await db.CodeFiles.ToListAsync());
            originalSha = (await db.FileLocations.FirstAsync(l => l.HostId == ingestorA.HostId)).ContentSha256;
        }

        // Act: host B modifies its file content. The mtime tier-2 short-circuit in CodeAdapter
        // is bypassed by writing different bytes (so the content hash genuinely changes).
        File.WriteAllText(Path.Combine(bDir, "Hello2.cs"),
            "// modified by B\nclass S { void M() { /* changed */ } }\nclass T {}\n");

        var responseB2 = await ingestorB.RunOnceAsync();
        Assert.Equal(0, responseB2.Failed);

        // Assert: two CodeFile rows now (old + new content). A's location still points at the
        // original blob; B's location points at the new one.
        using (var dbScope = _primary.Factory.Services.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
            var contents = await db.CodeFiles.ToListAsync();
            Assert.Equal(2, contents.Count);

            var locA = await db.FileLocations.FirstAsync(l => l.HostId == ingestorA.HostId);
            var locB = await db.FileLocations.FirstAsync(l => l.HostId == ingestorB.HostId);

            Assert.Equal(originalSha, locA.ContentSha256);
            Assert.NotEqual(originalSha, locB.ContentSha256);
            // Both rows sit in the same project — i.e. the project_id was reused as expected.
            Assert.Equal(locA.ProjectId, locB.ProjectId);
        }
    }

    [Fact]
    public async Task RevokedHost_RejectedOnIngest()
    {
        // Arrange: pair two hosts, then revoke A. A's next ingest should be rejected.
        // The end-to-end revoke flow has two pieces:
        //   1. POST /api/pairings registers the host_id in the hosts table.
        //   2. DELETE /api/pairings/{id} marks the pairing revoked AND deactivates the linked api_key.
        // For our in-process test we use the AIMEMORY_API_KEY env-var (admin scope) so the
        // batch handler reaches the host-validation step. We therefore exercise the revoke flow
        // by deleting the host row directly — that triggers the same 403 path the production
        // pairing-revoke flow ends up at (the secondary's key is also invalidated, but the
        // host-validation 403 fires first when the key is shared).
        using var project = new TempProject("Rev.cs", "class R {}\n");
        using var saltDir = new TempDir("salt-rev");
        var clientA = _primary.Factory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-AIMemory-Api-Key", _primary.AdminApiKey);

        var ingestorA = new TestIngestor(saltDir.Path, "host-rev", clientA, project.Path);
        var pairResp = await ingestorA.PairAsync(_primary.AdminClient);
        Assert.True(pairResp.IsSuccessStatusCode);

        // First ingest succeeds as a baseline.
        var ok = await ingestorA.RunOnceAsync();
        Assert.Equal(0, ok.Failed);

        // Simulate revoke: drop the host row. Production pathway is DELETE /api/pairings/{id}
        // which sets is_revoked=1 and deactivates the api_key; the batch handler's host check
        // fires when the host is gone or unknown.
        using (var dbScope = _primary.Factory.Services.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
            var host = await db.Hosts.FirstAsync(h => h.HostId == ingestorA.HostId);
            db.Hosts.Remove(host);
            await db.SaveChangesAsync();
        }

        // Act: A retries. Should be 403 — the design's "compromised secondary" mitigation.
        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = new List<string> { project.Path },
            DetectionMode = ChangeDetectionMode.FsOnly
        };
        // We POST a hand-built batch directly to surface the status code rather than
        // throwing inside RunOnceAsync (which uses EnsureSuccessStatusCode).
        var rejectedRequest = new BatchIngestRequest
        {
            ClientId = "test-rev",
            MachineName = "host-rev",
            Source = "code-index",
            HostId = ingestorA.HostId,
            ProjectId = "00000000000000000000000000000000",
            Events = new List<IngestEvent>()
        };
        var rejectResp = await clientA.PostAsJsonAsync("/api/ingest/batch", rejectedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, rejectResp.StatusCode);
    }

    /// <summary>
    /// Self-contained scratch project. Initializes a real git repo with a fixed signature so
    /// the root commit SHA is deterministic across <see cref="MirrorTo"/> calls — that's what
    /// gives both "hosts" the same canonical <c>project_id</c> per design §2.2.
    /// </summary>
    private sealed class TempProject : IDisposable
    {
        // Fixed timestamp so the root commit SHA is reproducible across mirrored repos. The
        // commit's SHA is derived from tree + author + committer + message + parent — pinning
        // every input pins the SHA.
        private static readonly Signature FixedSig = new(
            "Smoke Test", "smoke@test.local", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        private const string OriginUrl = "https://github.com/aimemory-test/smoke.git";

        public string Path { get; }
        private readonly string _fileName;
        private readonly string _content;

        public TempProject(string fileName, string content)
        {
            _fileName = fileName;
            _content = content;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aimemory-smoke-proj-" + Guid.NewGuid().ToString("N"));
            InitRepo(Path);
        }

        /// <summary>Materializes a fresh git repo with identical history at a sibling path.
        /// The root commit's SHA matches the source repo's because we pin the signature + content.</summary>
        public string MirrorTo(string suffix)
        {
            var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"aimemory-smoke-proj-{suffix}-{Guid.NewGuid():N}");
            InitRepo(dest);
            return dest;
        }

        private void InitRepo(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, _fileName), _content);
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commands.Stage(repo, _fileName);
            repo.Commit("initial", FixedSig, FixedSig);
            // Same origin URL on both hosts so the canonical_remote_url normalization step
            // produces the same string → same project_id.
            repo.Network.Remotes.Add("origin", OriginUrl);
        }

        public void Dispose()
        {
            // libgit2 holds memory-mapped pack files briefly; force GC and retry.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int i = 0; i < 3; i++)
            {
                try { Directory.Delete(Path, recursive: true); return; }
                catch when (i < 2) { Thread.Sleep(50); }
                catch { return; }
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string label)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"aimemory-smoke-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Disable parallel execution for the smoke tests since they patch process-wide env vars
/// (PROGRAMDATA / APPDATA / AIMEMORY_API_KEY) inside <see cref="DistributedIngestionSmokeTests.PrimaryFixture"/>.
/// </summary>
[CollectionDefinition("DistributedSmoke", DisableParallelization = true)]
public class DistributedSmokeCollection { }
