using BackupNormalizer.Ui.Models;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class ExecutionSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "bn-execution-" + Guid.NewGuid().ToString("N")
    );
    private string Source => Path.Combine(_directory, "source");
    private string Target => Path.Combine(_directory, "target");
    private string Journal => Path.Combine(_directory, "execution.db");

    public ExecutionSessionTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
    }

    public void Dispose()
    {
        Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(_directory));
        Directory.Delete(_directory, true);
    }

    private PlanDoc CopyPlan()
    {
        File.WriteAllBytes(Path.Combine(Source, "a.bin"), new byte[1024 * 1024]);
        File.WriteAllText(Path.Combine(Source, "b.txt"), "second file");
        return new PlanDoc(
            "copy-plan",
            Database.UtcNow(),
            1024 * 1024 + 11,
            null,
            "source",
            Source,
            "target",
            Target,
            [CopyOperation(1, "a.bin"), CopyOperation(2, "b.txt")]
        );
    }

    private PlanOpDoc CopyOperation(int id, string name)
    {
        var file = new FileInfo(Path.Combine(Source, name));
        return new PlanOpDoc(
            id,
            OpType.Copy,
            SourceScope.Source,
            "source",
            name,
            "target",
            name,
            file.Length,
            HasherFactory.Create("sha256").HashFile(file.FullName, file.Length)
        );
    }

    private ExecutionRunRequest Request(PlanDoc plan, bool resume = false, bool verify = false) =>
        new(plan, Journal, Source, Target, Resume: resume, VerifyOnly: verify);

    [Fact]
    public async Task Cancellation_Finishes_The_Current_Copy_And_Reopened_Execution_Resumes()
    {
        var plan = CopyPlan();
        using var cancellation = new CancellationTokenSource();
        var updates = new List<ExecutionProgress>();
        var runner = new ExecutionJobRunner();
        var result = await runner.RunAsync(
            Request(plan),
            new InlineProgress(value =>
            {
                updates.Add(value);
                if (value.Position == 1 && value.Status == OpStatus.Started)
                {
                    cancellation.Cancel();
                }
            }),
            cancellation.Token
        );

        Assert.Equal("Canceled", result.Outcome);
        Assert.Equal(new Executor.ExecSummary(1, 0, 0, 0, true), result.Execution);
        Assert.True(File.Exists(Path.Combine(Target, "a.bin")));
        Assert.False(File.Exists(Path.Combine(Target, "b.txt")));
        Assert.Contains(updates, value => value.BytesCopied == 1024 * 1024);
        Assert.Contains(updates, value => value.BytesRead >= 1024 * 1024);
        Assert.Empty(Directory.GetFiles(Target, "*.tmp"));
        using (var db = Database.OpenReadOnly(Journal, pooling: false))
        {
            Assert.Equal(PlanStatus.Partial, db.GetPlan(plan.PlanId)!.Status);
        }

        var saved = ExecutionSession.Load(Journal);
        Assert.True(saved.RootsBound);
        Assert.Equal(OpStatus.Completed, saved.Operations[0].Status);
        Assert.Equal(OpStatus.Planned, saved.Operations[1].Status);
        Assert.NotEmpty(saved.Logs);
        var resumedUpdates = new List<ExecutionProgress>();
        var resumed = await runner.RunAsync(
            Request(saved.Document, resume: true),
            new InlineProgress(resumedUpdates.Add)
        );
        Assert.Equal("Completed", resumed.Outcome);
        Assert.Equal(new Executor.ExecSummary(2, 0, 0, 0), resumed.Execution);
        Assert.Equal(2, resumed.Verification!.Ok);
        Assert.Equal(0, resumed.Verification.Bad);
        Assert.Contains(resumedUpdates, value => value.BytesCopied == 11);
        Assert.All(resumed.Session!.Operations, op => Assert.Equal(OpStatus.Completed, op.Status));
        Assert.True(resumed.Session.Logs.Count > saved.Logs.Count);
    }

    [Fact]
    public async Task Resume_Rejects_Changed_Roots_And_Changed_Operations()
    {
        var plan = CopyPlan();
        var runner = new ExecutionJobRunner();
        Assert.Equal("Completed", (await runner.RunAsync(Request(plan))).Outcome);
        string anotherTarget = Path.Combine(_directory, "another-target");
        Directory.CreateDirectory(anotherTarget);
        var changedRoot = await runner.RunAsync(
            Request(plan, resume: true) with
            {
                TargetPath = anotherTarget,
            }
        );
        Assert.Equal("Failed", changedRoot.Outcome);
        Assert.Contains("bound to its original roots", changedRoot.Message);
        Assert.Empty(Directory.GetFiles(anotherTarget));

        var changedPlan = plan with
        {
            Operations = [plan.Operations[0] with { ExpectedHash = new string('f', 64) }],
        };
        var changedOperations = await runner.RunAsync(Request(changedPlan, resume: true));
        Assert.Equal("Failed", changedOperations.Outcome);
        Assert.Contains("does not match", changedOperations.Message);
    }

    [Fact]
    public async Task Inventory_Database_Is_Rejected_Without_Changes()
    {
        var plan = CopyPlan();
        using (var db = Database.OpenWritable(Journal, pooling: false))
        {
            PlanStaging.WriteToDatabase(db, plan, "target", Target);
            File.WriteAllText(Path.Combine(Target, "inventory.txt"), "inventory content");
            new Scanner(db, usnMode: "off").ScanRoot("target");
        }
        byte[] before = File.ReadAllBytes(Journal);
        var result = await new ExecutionJobRunner().RunAsync(Request(plan, resume: true));
        Assert.Equal("Failed", result.Outcome);
        Assert.Contains("not an inventory database", result.Message);
        Assert.Equal(before, File.ReadAllBytes(Journal));
        Assert.False(File.Exists(Path.Combine(Target, "a.bin")));
    }

    [Fact]
    public async Task Conflict_Stops_Execution_Without_Overwriting_And_Verification_Detects_Drift()
    {
        var plan = CopyPlan();
        File.WriteAllText(Path.Combine(Target, "a.bin"), "preserve existing content");
        var runner = new ExecutionJobRunner();
        var result = await runner.RunAsync(Request(plan));
        Assert.Equal("Partial", result.Outcome);
        Assert.Equal(1, result.Execution!.Conflicts);
        Assert.Equal("preserve existing content", File.ReadAllText(Path.Combine(Target, "a.bin")));
        Assert.Equal(OpStatus.Planned, result.Session!.Operations[1].Status);
        Assert.Null(result.Verification);

        File.WriteAllBytes(Path.Combine(Target, "a.bin"), new byte[1024 * 1024]);
        Assert.Equal("Completed", (await runner.RunAsync(Request(plan, resume: true))).Outcome);
        Directory.Move(Source, Source + "-offline");
        File.WriteAllText(Path.Combine(Target, "b.txt"), "changed now");
        var verified = await runner.RunAsync(Request(plan, verify: true));
        Assert.Equal("Partial", verified.Outcome);
        Assert.Equal(1, verified.Verification!.Ok);
        Assert.Equal(1, verified.Verification.Bad);
        Assert.Contains("HASH-MISMATCH", Assert.Single(verified.Verification.Issues).Message);
    }

    [Fact]
    public async Task Verification_Uses_Final_Destinations_After_An_Ordered_Copy_And_Move()
    {
        File.WriteAllText(Path.Combine(Target, "original.txt"), "ordered content");
        var staged = new List<PlanStaging.StagedOp>();
        staged.AddRange(
            PlanStaging.StageCopy(
                Target,
                Path.Combine(Target, "original.txt"),
                Path.Combine(Target, "middle")
            )
        );
        var copy = staged.Single(op => op.Type == OpType.Copy);
        staged.Add(
            new PlanStaging.StagedOp(
                OpType.Move,
                "middle/original.txt",
                "final.txt",
                copy.ExpectedSize,
                copy.ExpectedHash
            )
        );
        var plan = PlanStaging.BuildPlanDoc("ordered", "target", Target, staged);
        var result = await new ExecutionJobRunner().RunAsync(
            new ExecutionRunRequest(plan, Journal, Target, Target)
        );
        Assert.Equal("Completed", result.Outcome);
        Assert.Equal(1, result.Verification!.Ok);
        Assert.Equal(2, result.Verification.Skipped);
        Assert.False(File.Exists(Path.Combine(Target, "middle", "original.txt")));
        Assert.Equal("ordered content", File.ReadAllText(Path.Combine(Target, "final.txt")));
        Assert.Equal(0, Cli.Run(["verify", "ordered", "--db", Journal]));
    }

    [Fact]
    public async Task Journal_Inside_An_Operated_Tree_And_PreCanceled_Run_Do_Not_Change_Files()
    {
        var plan = CopyPlan();
        var runner = new ExecutionJobRunner();
        var nested = await runner.RunAsync(
            Request(plan) with
            {
                DatabasePath = Path.Combine(Target, "unsafe.db"),
            }
        );
        Assert.Equal("Failed", nested.Outcome);
        Assert.Contains("outside the source and target", nested.Message);
        Assert.Empty(Directory.GetFiles(Target));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var result = await runner.RunAsync(Request(plan), cancellationToken: canceled.Token);
        Assert.Equal("Canceled", result.Outcome);
        Assert.False(File.Exists(Journal));
        Assert.Empty(Directory.GetFiles(Target));
    }

    private sealed class InlineProgress(Action<ExecutionProgress> report)
        : IProgress<ExecutionProgress>
    {
        public void Report(ExecutionProgress value) => report(value);
    }
}
