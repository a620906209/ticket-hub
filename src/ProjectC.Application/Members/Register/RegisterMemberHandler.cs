using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Members.Register;

public sealed class RegisterMemberHandler
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IValidator<RegisterMemberRequest> _validator;

    public RegisterMemberHandler(
        IApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        IValidator<RegisterMemberRequest> validator)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _validator = validator;
    }

    public async Task<Result<Guid>> HandleAsync(RegisterMemberRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var emailExists = await _dbContext.Members.AnyAsync(m => m.Email == request.Email, cancellationToken);
        if (emailExists)
        {
            return Result<Guid>.Failure(Error.Conflict("此 Email 已被註冊。"));
        }

        var passwordHash = _passwordHasher.HashPassword(request.Password);
        var member = Member.Register(request.Email, request.DisplayName, passwordHash);

        _dbContext.Members.Add(member);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result<Guid>.Success(member.Id);
    }
}
