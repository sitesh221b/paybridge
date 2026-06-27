using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayBridge.PaymentApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_payments_merchant_idempotency",
                table: "payments");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ux_payments_merchant_idempotency",
                table: "payments",
                columns: new[] { "tenant_id", "merchant_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_payments_merchant_idempotency",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "payments");

            migrationBuilder.CreateIndex(
                name: "ux_payments_merchant_idempotency",
                table: "payments",
                columns: new[] { "merchant_id", "idempotency_key" },
                unique: true);
        }
    }
}
