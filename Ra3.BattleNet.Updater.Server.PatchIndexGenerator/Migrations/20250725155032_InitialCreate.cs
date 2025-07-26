using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ra3.BattleNet.Updater.Server.PatchIndexGenerator.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatchIndexes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileGuid = table.Column<byte[]>(type: "BLOB", maxLength: 16, nullable: false),
                    OldContentHash = table.Column<byte[]>(type: "BLOB", maxLength: 16, nullable: false),
                    NewContentHash = table.Column<byte[]>(type: "BLOB", maxLength: 16, nullable: false),
                    PatchName = table.Column<byte[]>(type: "BLOB", maxLength: 16, nullable: false),
                    AddedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MajorVersion = table.Column<byte>(type: "INTEGER", nullable: false),
                    MinorVersion = table.Column<byte>(type: "INTEGER", nullable: false),
                    BuildVersion = table.Column<byte>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchIndexes", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatchIndexes");
        }
    }
}
