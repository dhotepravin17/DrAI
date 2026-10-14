using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartQueue.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlansAnalyticsRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "Doctors",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Free");

            migrationBuilder.CreateTable(
                name: "ProductEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Value = table.Column<double>(type: "float", nullable: true),
                    PropertiesJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductEvents_Name_OccurredAt",
                table: "ProductEvents",
                columns: new[] { "Name", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductEvents_OccurredAt",
                table: "ProductEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductEvents");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "Doctors");
        }
    }
}
