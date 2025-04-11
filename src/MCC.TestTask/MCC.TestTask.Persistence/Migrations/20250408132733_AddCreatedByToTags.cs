using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCC.TestTask.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatedByToTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "Tags",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "Tags");
        }
    }
}
