using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastDrop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalScanTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextScanAttemptAt",
                table: "TransferSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanAttemptCount",
                table: "TransferSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScannerReference",
                table: "TransferSessions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextScanAttemptAt",
                table: "TransferSessions");

            migrationBuilder.DropColumn(
                name: "ScanAttemptCount",
                table: "TransferSessions");

            migrationBuilder.DropColumn(
                name: "ScannerReference",
                table: "TransferSessions");
        }
    }
}
