# Pokrycie kryteriów akceptacji testami (generowane: tools/ac-coverage.py)

AC Must: 33/59 z co najmniej jednym testem oznaczonym `Trait("AC", …)`. Uwaga: test oznaczony ≠ AC spełnione — wiele AC wymaga E2E/Windows (kolejka budowa/do-uruchomienia-na-windows.md).

| AC | US | Kat. | Test | Pliki |
|---|---|---|---|---|
| AC-01.1 | US-01 | M | tak | tests/Kobian.Tests.Unit/Ux/WizardAndValidationTests.cs |
| AC-01.2 | US-01 | M | tak | tests/Kobian.Tests.UI/WizardFtpsTests.cs, tests/Kobian.Tests.UI/WizardScreenTests.cs, tests/Kobian.Tests.Unit/Storage/SmbStorageTests.cs, tests/Kobian.Tests.Unit/Ux/WizardAndValidationTests.cs, tests/Kobian.Tests.Unit/Ux/WizardViewModelTests.cs |
| AC-01.3 | US-01 | M | tak | tests/Kobian.Tests.UI/WizardDatabaseTests.cs, tests/Kobian.Tests.UI/WizardFtpsTests.cs, tests/Kobian.Tests.UI/WizardScreenTests.cs, tests/Kobian.Tests.Unit/Agent/DatabaseDiscoveryTests.cs, tests/Kobian.Tests.Unit/Agent/FtpAgentWiringTests.cs, tests/Kobian.Tests.Unit/Agent/JobDestinationTests.cs, tests/Kobian.Tests.Unit/Agent/JobTransferTests.cs, tests/Kobian.Tests.Unit/Agent/WizardDatabaseOverPipeTests.cs |
| AC-02.1 | US-02 | M | tak | tests/Kobian.Tests.Unit/Agent/MsSqlJobTests.cs, tests/Kobian.Tests.Unit/Sources/ControlQueryTests.cs, tests/Kobian.Tests.Unit/Sources/MsSqlBackupServiceTests.cs, tests/Kobian.Tests.Unit/Sources/MsSqlTests.cs, tests/Kobian.Tests.Unit/Sources/StagingAndChainTests.cs |
| AC-02.2 | US-02 | M | tak | tests/Kobian.Tests.Unit/Sources/MsSqlBackupServiceTests.cs |
| AC-02.3 | US-02 | M | tak | tests/Kobian.Tests.UI/WizardDatabaseTests.cs, tests/Kobian.Tests.Unit/Agent/DatabaseDiscoveryTests.cs, tests/Kobian.Tests.Unit/Agent/MsSqlJobTests.cs, tests/Kobian.Tests.Unit/Agent/WizardOverPipeTests.cs, tests/Kobian.Tests.Unit/Sources/MsSqlBackupServiceTests.cs, tests/Kobian.Tests.Unit/Sources/MsSqlDiscoveryTests.cs, tests/Kobian.Tests.Unit/Sources/MsSqlTests.cs |
| AC-03.1 | US-03 | M | tak | tests/Kobian.Tests.Unit/Agent/CatalogSchedulerTests.cs, tests/Kobian.Tests.Unit/Scheduler/SchedulerTests.cs |
| AC-03.2 | US-03 | M | — |  |
| AC-03.3 | US-03 | M | tak | tests/Kobian.Tests.Unit/Agent/AgentGrpcTests.cs, tests/Kobian.Tests.Unit/Agent/CatalogSchedulerTests.cs, tests/Kobian.Tests.Unit/Cli/CliAppTests.cs, tests/Kobian.Tests.Unit/Scheduler/SchedulerTests.cs |
| AC-04.1 | US-04 | M | tak | tests/Kobian.Tests.UI/WizardScreenTests.cs, tests/Kobian.Tests.Unit/Agent/ServerIdentityGateTests.cs, tests/Kobian.Tests.Unit/Agent/ServerIdentityMachineTests.cs, tests/Kobian.Tests.Unit/Agent/ServerIdentityStoreTests.cs, tests/Kobian.Tests.Unit/Agent/WizardBackendTests.cs, tests/Kobian.Tests.Unit/Storage/SshNetAdapterTests.cs |
| AC-04.2 | US-04 | M | — |  |
| AC-04.3 | US-04 | M | tak | tests/Kobian.Tests.Unit/Storage/SmbStorageTests.cs |
| AC-05.1 | US-05 | M | tak | tests/Kobian.Tests.Unit/Agent/AlertActionTests.cs, tests/Kobian.Tests.Unit/Notifications/AlertServiceTests.cs, tests/Kobian.Tests.Unit/Notifications/NotificationTests.cs |
| AC-05.2 | US-05 | M | — |  |
| AC-05.3 | US-05 | M | — |  |
| AC-06.1 | US-06 | M | — |  |
| AC-06.2 | US-06 | M | — |  |
| AC-07.1 | US-07 | M | tak | tests/Kobian.Tests.UI/RestoreScreenTests.cs, tests/Kobian.Tests.UI/SnapshotBrowserTests.cs, tests/Kobian.Tests.Unit/Agent/CatalogRestoreServiceTests.cs, tests/Kobian.Tests.Unit/Agent/RestoreJournalTests.cs, tests/Kobian.Tests.Unit/Agent/RestorePlanTests.cs, tests/Kobian.Tests.Unit/Restore/EndToEndTests.cs, tests/Kobian.Tests.Unit/Restore/RestoreExecutorTests.cs |
| AC-07.2 | US-07 | M | tak | tests/Kobian.Tests.UI/RestoreScreenTests.cs, tests/Kobian.Tests.Unit/Agent/CatalogRestoreServiceTests.cs, tests/Kobian.Tests.Unit/Agent/RestorePlanTests.cs, tests/Kobian.Tests.Unit/Agent/RestoreTargetAuthorizationTests.cs |
| AC-07.3 | US-07 | M | tak | tests/Kobian.Tests.Unit/Agent/CatalogRestoreServiceTests.cs, tests/Kobian.Tests.Unit/Agent/RestoreJournalTests.cs, tests/Kobian.Tests.Unit/Catalog/RestoreJournalSchemaTests.cs |
| AC-08.1 | US-08 | M | tak | tests/Kobian.Tests.Unit/Agent/CatalogJobServiceTests.cs, tests/Kobian.Tests.Unit/Engine/BackupPipelineTests.cs, tests/Kobian.Tests.Unit/Restore/EndToEndTests.cs |
| AC-08.2 | US-08 | M | — |  |
| AC-08.3 | US-08 | M | — |  |
| AC-09.1 | US-09 | M | tak | tests/Kobian.Tests.Unit/Agent/CatalogJobServiceTests.cs, tests/Kobian.Tests.Unit/Agent/VerifyScheduleTests.cs, tests/Kobian.Tests.Unit/Cli/CliAppTests.cs, tests/Kobian.Tests.Unit/Engine/RepoCheckerTests.cs |
| AC-09.2 | US-09 | M | — |  |
| AC-10.1 | US-10 | M | tak | tests/Kobian.Tests.Unit/Agent/CatalogRetentionServiceTests.cs, tests/Kobian.Tests.Unit/Agent/RetentionHoldTests.cs, tests/Kobian.Tests.Unit/Retention/RetentionPlannerTests.cs, tests/Kobian.Tests.Unit/Ux/WizardAndValidationTests.cs |
| AC-10.2 | US-10 | M | — |  |
| AC-18.1 | US-18 | M | tak | tests/Kobian.Tests.Unit/Storage/FtpStorageTests.cs, tests/Kobian.Tests.Unit/Storage/LocalStorageTests.cs, tests/Kobian.Tests.Unit/Storage/SftpStorageTests.cs |
| AC-28.1 | US-28 | M | tak | tests/Kobian.Tests.UI/WizardScreenTests.cs, tests/Kobian.Tests.Unit/Agent/WizardOverPipeTests.cs, tests/Kobian.Tests.Unit/Ux/WizardAndValidationTests.cs, tests/Kobian.Tests.Unit/Ux/WizardViewModelTests.cs |
| AC-28.2 | US-28 | M | — |  |
| AC-29.1 | US-29 | M | tak | tests/Kobian.Tests.Unit/Agent/JobTransferTests.cs |
| AC-29.2 | US-29 | M | tak | tests/Kobian.Tests.Unit/Agent/JobTransferTests.cs |
| AC-30.1 | US-30 | M | tak | tests/Kobian.Tests.Unit/Sources/MsSqlTests.cs |
| AC-30.2 | US-30 | M | — |  |
| AC-31.1 | US-31 | M | tak | tests/Kobian.Tests.UI/SettingsScreenTests.cs |
| AC-32.1 | US-32 | M | tak | tests/Kobian.Tests.UI/DbRestoreScreenTests.cs, tests/Kobian.Tests.Unit/Agent/MsSqlJobTests.cs, tests/Kobian.Tests.Unit/Sources/MsSqlRestoreServiceTests.cs |
| AC-32.2 | US-32 | M | tak | tests/Kobian.Tests.UI/DbRestoreScreenTests.cs, tests/Kobian.Tests.Unit/Sources/MsSqlRestoreServiceTests.cs |
| AC-33.1 | US-33 | M | — |  |
| AC-33.2 | US-33 | M | — |  |
| AC-37.1 | US-37 | M | tak | tests/Kobian.Tests.Unit/Agent/AgentGrpcTests.cs, tests/Kobian.Tests.Unit/Agent/CatalogRestoreServiceTests.cs, tests/Kobian.Tests.Unit/Agent/JobTransferTests.cs, tests/Kobian.Tests.Unit/Cli/CliAppTests.cs |
| AC-38.1 | US-38 | M | — |  |
| AC-43.1 | US-43 | M | — |  |
| AC-34.1 | US-34 | ? | — |  |
| AC-28.3 | US-28 | M | — |  |
| AC-01.4 | US-01 | M | — |  |
| AC-01.5 | US-01 | M | — |  |
| AC-37.2 | US-37 | M | tak | tests/Kobian.Tests.Unit/Agent/AgentGrpcTests.cs, tests/Kobian.Tests.Unit/Cli/CliAppTests.cs |
| AC-34.2 | US-34 | ? | — |  |
| AC-05.4 | US-05 | M | — |  |
| AC-37.3 | US-37 | M | tak | tests/Kobian.Tests.Unit/Agent/AgentServiceHostTests.cs |
| AC-08.4 | US-08 | M | tak | tests/Kobian.Tests.Unit/Agent/CatalogJobServiceTests.cs, tests/Kobian.Tests.Unit/Engine/BackupPipelineTests.cs, tests/Kobian.Tests.Unit/Notifications/RunFinishedEventTests.cs, tests/Kobian.Tests.Unit/Scheduler/RunExecutorTests.cs |
| AC-08.5 | US-08 | M | — |  |
| AC-08.6 | US-08 | M | — |  |
| AC-10.3 | US-10 | M | tak | tests/Kobian.Tests.Unit/Agent/CatalogPruneServiceTests.cs, tests/Kobian.Tests.Unit/Engine/IndexConsolidationTests.cs, tests/Kobian.Tests.Unit/Engine/PrunePlannerTests.cs |
| AC-11.1 | US-11 | ? | tak | tests/Kobian.Tests.Unit/Scheduler/RunExecutorTests.cs |
| AC-08.7 | US-08 | M | tak | tests/Kobian.Tests.Unit/Format/MetadataTests.cs, tests/Kobian.Tests.Unit/Restore/RestoreMappingTests.cs |
| AC-38.2 | US-38 | M | — |  |
| AC-04.4 | US-04 | M | — |  |
| AC-12.1 | US-12 | ? | — |  |
| AC-04.5 | US-04 | M | tak | tests/Kobian.Tests.Unit/Storage/FtpStorageTests.cs, tests/Kobian.Tests.Unit/Storage/SftpStorageTests.cs |
| AC-04.6 | US-04 | M | tak | tests/Kobian.Tests.Unit/Storage/SftpStorageTests.cs |
| AC-12.2 | US-12 | ? | tak | tests/Kobian.Tests.Unit/Agent/ServerIdentityGateTests.cs, tests/Kobian.Tests.Unit/Agent/ServerIdentityMachineTests.cs, tests/Kobian.Tests.Unit/Storage/FluentFtpAdapterTests.cs, tests/Kobian.Tests.Unit/Storage/FluentFtpLoopbackTests.cs, tests/Kobian.Tests.Unit/Storage/FtpStorageTests.cs, tests/Kobian.Tests.Unit/Storage/PinListOptionsTests.cs |
| AC-18.2 | US-18 | M | — |  |
| AC-18.3 | US-18 | M | — |  |
