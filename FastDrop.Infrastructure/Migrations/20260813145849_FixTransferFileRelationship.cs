using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastDrop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixTransferFileRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TransferSessions_Files_FileId",
                table: "TransferSessions");

            migrationBuilder.DropIndex(
                name: "IX_TransferSessions_FileId",
                table: "TransferSessions");

            migrationBuilder.DropColumn(
                name: "FileId",
                table: "TransferSessions");

            migrationBuilder.AddColumn<Guid>(
                name: "TransferSessionId",
                table: "Files",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Files_TransferSessionId",
                table: "Files",
                column: "TransferSessionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Files_TransferSessions_TransferSessionId",
                table: "Files",
                column: "TransferSessionId",
                principalTable: "TransferSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Files_TransferSessions_TransferSessionId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_TransferSessionId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "TransferSessionId",
                table: "Files");

            migrationBuilder.AddColumn<Guid>(
                name: "FileId",
                table: "TransferSessions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_TransferSessions_FileId",
                table: "TransferSessions",
                column: "FileId");

            migrationBuilder.AddForeignKey(
                name: "FK_TransferSessions_Files_FileId",
                table: "TransferSessions",
                column: "FileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
