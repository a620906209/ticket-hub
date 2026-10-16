namespace ProjectC.Application.Venues.CreateSeatMap;

public sealed record CreateSeatMapRequest(IReadOnlyList<SeatRequest> Seats);

// 欄位刻意宣告為 nullable：非 nullable string 會讓 [ApiController] 的 MVC 模型驗證隱含 [Required] 並逐筆走訪所有座位，
// 在 FluentValidation 的座位數上限之前就回逐筆錯誤；null／空白改由 SeatRequestValidator 的 NotEmpty 拒絕
// （request-body-size-limits design.md 決策 7）。
public sealed record SeatRequest(string? ZoneCode, string? SeatNumber);
