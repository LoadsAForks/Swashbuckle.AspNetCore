﻿using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Swashbuckle.AspNetCore.TestSupport;

[ModelMetadataType(typeof(MetadataType))]
public class TypeWithValidationAttributesViaMetadataType
{
    public string StringWithDataTypeCreditCard { get; set; }

    public string StringWithMinMaxLength { get; set; }

    public string[] ArrayWithMinMaxLength { get; set; }

    public string StringWithLength { get; set; }

    public string[] ArrayWithLength { get; set; }

    public string StringWithBase64 { get; set; }

    public double IntWithExclusiveRange { get; set; }

    public int IntWithRange { get; set; }

    public string StringWithRegularExpression { get; set; }

    public string StringWithStringLength { get; set; }

    public string StringWithRequired { get; set; }

    public string StringWithRequiredAllowEmptyTrue { get; set; }

    public string StringWithDescription { get; set; }

    public string StringWithReadOnly { get; set; }

    public IntEnum? NullableIntEnumWithRequired { get; set; }
}

public class MetadataType
{
    [DataType(DataType.CreditCard)]
    public string StringWithDataTypeCreditCard { get; set; }

    [MinLength(1), MaxLength(3)]
    public string StringWithMinMaxLength { get; set; }

    [MinLength(1), MaxLength(3)]
    public string[] ArrayWithMinMaxLength { get; set; }

    [Length(1, 3)]
    public string StringWithLength { get; set; }

    [Length(1, 3)]
    public string[] ArrayWithLength { get; set; }

    [Range(1, 10, MinimumIsExclusive = true, MaximumIsExclusive = true)]
    public int IntWithExclusiveRange { get; set; }

    [Base64String]
    public string StringWithBase64 { get; set; }

    [Range(1, 10)]
    public int IntWithRange { get; set; }

    [RegularExpression("^[3-6]?\\d{12,15}$")]
    public string StringWithRegularExpression { get; set; }

    [StringLength(10, MinimumLength = 5)]
    public string StringWithStringLength { get; set; }

    [Required]
    public string StringWithRequired { get; set; }

    [Required(AllowEmptyStrings = true)]
    public string StringWithRequiredAllowEmptyTrue { get; set; }

    [Description("Description")]
    public string StringWithDescription { get; set; }

    [ReadOnly(true)]
    public string StringWithReadOnly { get; set; }

    [Required]
    public IntEnum? NullableIntEnumWithRequired { get; set; }
}
