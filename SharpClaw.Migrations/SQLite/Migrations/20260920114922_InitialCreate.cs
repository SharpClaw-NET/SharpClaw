using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Migrations.SQLite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configuration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ApiEndpoint = table.Column<string>(type: "TEXT", nullable: true),
                    EncryptedApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScopedStorageIndexes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StorageName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IndexName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RecordKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StringValue = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    NumberValue = table.Column<double>(type: "REAL", nullable: true),
                    DateTimeValue = table.Column<long>(type: "INTEGER", nullable: true),
                    BoolValue = table.Column<bool>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScopedStorageIndexes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScopedStorageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StorageName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RecordKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScopedStorageRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CapabilityTagsRaw = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Configuration_SourceId_Key",
                table: "Configuration",
                columns: new[] { "SourceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_Name_ProviderId",
                table: "Models",
                columns: new[] { "Name", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_ProviderId",
                table: "Models",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Providers_Name",
                table: "Providers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationStates_SourceId",
                table: "RegistrationStates",
                column: "SourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScopedStorageIndexes_SourceId_StorageName_IndexName_BoolValue_RecordKey",
                table: "ScopedStorageIndexes",
                columns: new[] { "SourceId", "StorageName", "IndexName", "BoolValue", "RecordKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopedStorageIndexes_SourceId_StorageName_IndexName_DateTimeValue_RecordKey",
                table: "ScopedStorageIndexes",
                columns: new[] { "SourceId", "StorageName", "IndexName", "DateTimeValue", "RecordKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopedStorageIndexes_SourceId_StorageName_IndexName_NumberValue_RecordKey",
                table: "ScopedStorageIndexes",
                columns: new[] { "SourceId", "StorageName", "IndexName", "NumberValue", "RecordKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopedStorageIndexes_SourceId_StorageName_IndexName_StringValue_RecordKey",
                table: "ScopedStorageIndexes",
                columns: new[] { "SourceId", "StorageName", "IndexName", "StringValue", "RecordKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopedStorageIndexes_SourceId_StorageName_RecordKey",
                table: "ScopedStorageIndexes",
                columns: new[] { "SourceId", "StorageName", "RecordKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopedStorageRecords_SourceId_StorageName_RecordKey",
                table: "ScopedStorageRecords",
                columns: new[] { "SourceId", "StorageName", "RecordKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Configuration");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "RegistrationStates");

            migrationBuilder.DropTable(
                name: "ScopedStorageIndexes");

            migrationBuilder.DropTable(
                name: "ScopedStorageRecords");

            migrationBuilder.DropTable(
                name: "Providers");
        }
    }
}
