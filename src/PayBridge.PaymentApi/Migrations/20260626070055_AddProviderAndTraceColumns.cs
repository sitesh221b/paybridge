using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayBridge.PaymentApi.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAndTraceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "original_span_id",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_trace_id",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_transaction_id",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "original_span_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "original_trace_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "provider_transaction_id",
                table: "payments");
        }
    }
}
