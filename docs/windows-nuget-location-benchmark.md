# Windows NuGet cache location pilot

Measured October 24, 2026. **Relocating both NuGet caches produced substantially
shorter cold restores in this bounded pilot, including the closest hardware
comparison. This is not evidence of a production warm-cache speedup.**
At the pilot's conclusion production workflows were unchanged; no optimization
PR, release, signing, publishing, or blog edit had been performed.

## Production follow-up

Following explicit approval after the pilot, this branch now proposes the
minimal cache-location change for Windows Bridge tests, desktop CI, and Store
package builds. The one-off pilot workflow has been removed from the production
diff; its original commit and public run below preserve the experiment.
`scripts/windows/Initialize-NuGetCache.ps1` runs before SDK setup on GitHub-hosted
Windows only, creates missing package and HTTP cache directories under
`RUNNER_TEMP`, and preserves each explicitly configured cache variable.
Self-hosted and non-Windows environments are not relocated.

All three existing package-cache actions now follow `env.NUGET_PACKAGES`,
falling back to the original profile path when relocation is not applicable.
The `nuget-v2` key namespace intentionally cold-fills rather than restoring an
archive created for the previous absolute path/cache version. Existing
OS/architecture/SDK/workload/dependency scoping and prefix fallback remain.
Only packages are remotely persisted, never the HTTP cache. API-only Store
diagnostics/submission jobs are unchanged, as are isolated SDK setup and pins,
native checks, signing, approval, and release gates.

This is a user-approved production candidate, **not a demonstrated warm-cache
speedup**. Normal PR CI supplies build and cache-save correctness evidence
separately; the bounded pilot is not being rerun. No Store/release workflow or
blog update is dispatched by this follow-up.

## Evidence and controls

[Public workflow run](https://github.com/jamesmontemagno/my-file-converter/actions/runs/40139644621)
completed all four jobs successfully. The experiment stopped at two trials per
variant, with no retries. The branch-only workflow also refuses subsequent run
numbers and attempts. It is a pilot, not a workflow to merge into main.

Current main was `1c9bf0916fa8fc707f09753e57ab69aa98f90e76`, containing the
already-merged [PR #59](https://github.com/jamesmontemagno/my-file-converter/pull/59).
All four jobs checked out benchmark commit
`e7a26adf02ea56c48dc84336d0ac3be40bdb9b13`, whose only change from main was the
new pilot workflow. Application source was identical.

Every job used Windows Server 2025 Datacenter, `windows-latest`, image
`win25-vs2026` version `20261021.278.1`, X64 runner architecture, SDK `10.0.401`,
and workload set `10.0.401.1`. The SDK was installed at
`D:\a\_temp\localmorph-dotnet` in **both** variants, with explicit SDK selection,
path verification, and a check against MSI installer state. The installed
`maui-windows` manifest was `10.0.110/10.0.100` in every job.

The pilot checked that both selected NuGet directories were absent or empty
before SDK/workload setup, and that the checkout contained no `bin`/`obj`
directories. Each matrix job ran on its own fresh hosted VM: no sequential
variant reused outputs, packages, HTTP cache, or an installed SDK.
Workload installation was allowed to populate the selected caches before app
restore, identically in both arms.

Remote NuGet caching was **disabled in both variants**. Remote lookup, download,
extraction, compression, and save were not performed; extract/save time was zero.
There was no warm-restored trial or interaction with existing production cache
keys. HTTP caches were not uploaded; artifacts contain only host/path metadata
and test reports, not packages, credentials, or build payloads.

The same Release desktop build ran with `WindowsOnly=true`, with restore split
from compilation (`--no-restore`). Both Bridge and Core projects were explicitly
restored, built, and tested with `--no-build --no-restore` on test execution.
All jobs installed the same FFmpeg/FFprobe `9.0.2-essentials_build-www.gyan.dev`.
Each job passed 15 Bridge and 297 Core tests, with the same three Apple-only
Core skips. Store-helper/provenance guards and native Explorer command
compilation for **both x64 and ARM64**, including DLL/PDB matching, also passed.
Store packaging and all release approval/signing/publishing paths were not run
or modified. Mac workflows were out of scope.

## Actual storage and hardware

These are measured paths, not assumptions about a drive letter.
`dotnet nuget locals all --list` verified both selected locations in every job.

| Resource | Default variant | Relocated variant |
|---|---|---|
| Global packages | `C:\Users\runneradmin\.nuget\packages` | `D:\a\_temp\nuget\packages` |
| HTTP cache | `C:\Users\runneradmin\AppData\Local\NuGet\v3-cache` | `D:\a\_temp\nuget\http-cache` |
| Workspace, both variants | `D:\a\my-file-converter\my-file-converter` | Same |
| Runner temp, both variants | `D:\a\_temp` | Same |
| Profile, both variants | `C:\Users\runneradmin` | Same |
| SDK, both variants | `D:\a\_temp\localmorph-dotnet` | Same |

Both C: and D: used NTFS and were distinct disks (C: disk 0; D: disk 1).
However, identical runner images did **not** mean identical hardware:

| Job | CPU (2 cores / 4 logical processors in every VM) | C: / D: disk model and bus | C: / D: volume size, bytes |
|---|---|---|---|
| default-1 | AMD EPYC 7763 64-Core Processor | Both `Msft Virtual Disk`, SAS | 160939618304 / 161059172352 |
| default-2 | AMD EPYC 7763 64-Core Processor | Both `Msft Virtual Disk`, SAS | 160939618304 / 161059172352 |
| runner-temp-1 | AMD EPYC 9V45 96-Core Processor | `Virtual_Disk NVME Premium` / `Microsoft NVMe Direct Disk v2`, both NVMe | 160939618304 / 236205371392 |
| runner-temp-2 | AMD EPYC 7763 64-Core Processor | Both `Msft Virtual Disk`, SAS | 160939618304 / 161059172352 |

Consequently, the closest hardware stratum is **two defaults versus one
relocated job**, not two matched pairs. Even that stratum matches only the broad
CPU/storage class, not the physical machines, host load, or network conditions.
The EPYC 9V45/NVMe sample must not be
used to attribute its faster general compilation/tests/setup to cache location.
Disk model/bus metadata establishes placement, not measured disk throughput;
there was no synthetic I/O benchmark or disk reconfiguration.

## Timings

Seconds below come from GitHub's job/step start and completion timestamps
(whole-second resolution), not the shorter test-framework or restore-internal
timers. Queue time is excluded. Total job execution includes runner provisioning/setup
visible in the job, diagnostics, checkout, tool setup, tests, artifacts, post
steps, and inter-step overhead. This is the entire **pilot job**, not the
duration of the production CI desktop job. Per-step medians need not sum to
the job median.

| Phase, seconds | default-1 | default-2 | runner-temp-1 (different hardware) | runner-temp-2 |
|---|---:|---:|---:|---:|
| Runner setup | 1 | 1 | 2 | 1 |
| Checkout | 11 | 9 | 9 | 10 |
| Host metadata and cache selection | 5 | 6 | 3 | 5 |
| Isolated SDK installation | 22 | 20 | 15 | 21 |
| SDK selection/path verification | 2 | 2 | 1 | 2 |
| Pinned workload installation/verification | 20 | 21 | 14 | 22 |
| Desktop restore | 61 | 53 | 9 | 10 |
| Desktop compilation, no restore | 48 | 52 | 26 | 46 |
| FFmpeg setup | 37 | 24 | 19 | 23 |
| Bridge + Core restore | 34 | 25 | 6 | 8 |
| Bridge + Core compilation, no restore | 6 | 6 | 3 | 6 |
| Bridge + Core test execution | 43 | 43 | 29 | 41 |
| Store-helper/provenance guard tests | 21 | 20 | 12 | 26 |
| Native x64 + ARM64 compile/PDB checks | 39 | 79 | 28 | 30 |
| Evidence artifact upload | 2 | 1 | 1 | 1 |
| Post SDK action | 0 | 0 | 0 | 0 |
| Post checkout | 2 | 3 | 2 | 2 |
| Complete job | 0 | 0 | 0 | 1 |
| Remote NuGet cache extract / save (not performed) | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| **Total execution** | **357 (5:57)** | **368 (6:08)** | **182 (3:02)** | **259 (4:19)** |
| **Per-job whole-minute ceiling** | **6** | **7** | **4** | **5** |

Descriptive pooled medians and ranges, **n=2 per arm**, all on the same image
but including the hardware difference:

| Phase | Default median [range], seconds | Relocated median [range], seconds |
|---|---:|---:|
| SDK installation | 21 [20-22] | 18 [15-21] |
| Workload installation/verification | 20.5 [20-21] | 18 [14-22] |
| Desktop restore | 57 [53-61] | 9.5 [9-10] |
| Desktop compilation | 50 [48-52] | 36 [26-46] |
| Test restore | 29.5 [25-34] | 7 [6-8] |
| Test compilation | 6 [6-6] | 4.5 [3-6] |
| Test execution | 43 [43-43] | 35 [29-41] |
| Native compilation/checks | 59 [39-79] | 29 [28-30] |
| Total execution | 362.5 [357-368] | 220.5 [182-259] |

For the closest hardware stratum (EPYC 7763/SAS), default **n=2** versus
relocated **n=1**:

| Phase | Default median [range], seconds | Relocated observation, seconds |
|---|---:|---:|
| SDK installation | 21 [20-22] | 21 |
| Workload installation/verification | 20.5 [20-21] | 22 |
| Desktop restore | 57 [53-61] | 10 |
| Desktop compilation | 50 [48-52] | 46 |
| Test restore | 29.5 [25-34] | 8 |
| Test compilation | 6 [6-6] | 6 |
| Test execution | 43 [43-43] | 41 |
| Native compilation/checks | 59 [39-79] | 30 |
| Total execution | 362.5 [357-368] | 259 |

The closest hardware sample's desktop restore was about **82% shorter** than
the default median; test restore was about **73% shorter**. SDK setup was
unchanged, workload setup was slightly slower, and desktop compilation improved
only modestly. This supports the cold-restore hypothesis more specifically than
claiming a workload-install or whole-build miracle. Native compilation varied
39-79 seconds even within the default arm, demonstrating unrelated variability.
No confidence intervals or statistical significance claims are justified by
this sample size.

Entire pilot execution was 357 and 368 seconds for defaults versus 259 seconds
for the broad-class-comparable relocated job: about **104 seconds shorter**
than the default midpoint of 362.5 seconds. That observation includes
diagnostics, provisioning/setup, restores, compilation, and tests, with the
uncontrolled variation above; it is not a measured production-CI reduction or
an attribution of all 104 seconds to NuGet placement.

### Whole-minute effect, not a billing claim

Using `ceil(job execution seconds / 60)`, defaults consumed 6 and 7 rounded
minutes; relocated jobs consumed 4 and 5. The observed two-job sums were
13 versus 9 rounded minutes, but that pooled difference includes different
hardware. In the closest hardware comparison, the relocated job rounded to
5 versus 6-7 for defaults: an observed 1-2 minute boundary difference per job,
**not a guaranteed saving**. Round each job, not a pooled median or summed
seconds. Actual combined execution of all four jobs was 1,166 seconds (19:26);
the four per-job ceilings sum to 22 minutes.

This is a public repository using standard hosted runners, whose execution is
[free under GitHub's public-repository rules](https://docs.github.com/en/billing/concepts/product-billing/github-actions).
These are execution/rounding observations, not billed amounts or a forecast of
private-repository savings. Artifact storage is separate.

## Applicability and decision

The [article's hypothesis](https://awakecoding.com/posts/speeding-up-dotnet-builds-on-windows-nuget-cache/)
applies to the observed placement: profile caches were on C: while workspace,
temp, and the already-isolated SDK were on D:. Relocating **both**
`NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH` was a promising cold-cache candidate.
This test does not isolate the individual contribution of each variable.

Do not credit the earlier PR #59 workload reduction (5:55 to 0:20, job 10:39
to 3:48) to this experiment. Both arms already used that isolated, pinned,
file-based SDK setup. Here the closest hardware workload times were 20-21
versus 22 seconds: effectively neutral, not another workload optimization.

At the time of the pilot, production Bridge CI restored `~/.nuget/packages` with
`actions/cache`; Windows Store packaging uses the same isolated toolchain and
was not dispatched. This cold-only pilot does not measure production warm
restore, cache extraction, or save performance, so **no production cache-path
change was made as part of the pilot**. The later user-approved candidate is
described above; it is not supported by a measured warm-cache gain. Existing
cache entries were not deleted.

If adopting the candidate later, guard it with `runner.os == 'Windows'` and
`runner.environment == 'github-hosted'`, respect existing explicit storage
overrides, and derive locations from `runner.temp` rather than hard-coding D:.
Confirm volumes on the target runner. Every corresponding `actions/cache`
path must follow `env.NUGET_PACKAGES`; otherwise the cache action restores the
old directory while NuGet uses the new one. Keep OS/architecture/SDK/workload/
dependency cache scoping, validate cross-path restore semantics rather than
assuming old entries will hit, and measure transfer separately. Do not remotely
persist the HTTP cache or authentication data.

No self-hosted runner, disk formatting, junction, Dev Drive, Defender change,
shared-cache deletion, release gate change, or additional platform matrix was
involved. The four-job budget is exhausted; no further trials are implied.

## Suggested factual blog addition (not published)

For [the existing optimization article](https://montemagno.com/reducing-github-actions-costs-ai-agents/):

> I also tried moving both NuGet's package and HTTP caches from the Windows
> profile to `runner.temp`, keeping LocalMorph's isolated SDK and pinned workload
> unchanged. Four fresh hosted jobs used the same source and runner image, with
> no remote cache restore or save. Hardware still varied: one relocated job
> landed on a newer CPU/NVMe host. In the closest hardware comparison, the two
> default desktop restores took 53 and 61 seconds; the relocated restore took
> 10 seconds. Workload setup stayed around 20-22 seconds. That is encouraging
> cold-restore evidence, not proof of a production warm-cache or billing saving.
> I left production CI unchanged rather than attribute all of the total-job
> difference to cache placement.

## Public job links and raw evidence

| Job | Public evidence |
|---|---|
| default-1 | [121008461682](https://github.com/jamesmontemagno/my-file-converter/actions/runs/40139644621/job/121008461682) |
| default-2 | [121008461856](https://github.com/jamesmontemagno/my-file-converter/actions/runs/40139644621/job/121008461856) |
| runner-temp-1 | [121008461992](https://github.com/jamesmontemagno/my-file-converter/actions/runs/40139644621/job/121008461992) |
| runner-temp-2 | [121008461852](https://github.com/jamesmontemagno/my-file-converter/actions/runs/40139644621/job/121008461852) |

The run's `nuget-location-{default,runner-temp}-{1,2}` artifacts contain sanitized
`metadata.json`, resolved `nuget-paths.txt`, and both test TRX reports (14-day
artifact retention). This report preserves the essential metadata and measured
tables beyond that artifact-retention window. GitHub step timestamps are also
available from the run's jobs API; no private billing export was used.
