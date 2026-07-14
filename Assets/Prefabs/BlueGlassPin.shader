Shader "Custom/Blue Glass Pin"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.02, 0.35, 1.0, 0.45)
        _RimColor("Rim Color", Color) = (0.25, 0.85, 1.0, 1.0)
        _EmissionColor("Emission Color", Color) = (0.0, 0.35, 1.0, 1.0)

        _Alpha("Transparency", Range(0, 1)) = 0.45
        _Smoothness("Smoothness", Range(0, 1)) = 0.95
        _RimPower("Rim Power", Range(0.5, 8)) = 2.2
        _RimIntensity("Rim Intensity", Range(0, 5)) = 2.0
        _EmissionIntensity("Emission Intensity", Range(0, 5)) = 0.8
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _RimColor;
                float4 _EmissionColor;
                float _Alpha;
                float _Smoothness;
                float _RimPower;
                float _RimIntensity;
                float _EmissionIntensity;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(IN.normalOS);

                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = normalize(normalInputs.normalWS);
                OUT.viewDirWS = normalize(GetWorldSpaceViewDir(posInputs.positionWS));

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(IN.viewDirWS);

                Light mainLight = GetMainLight();
                float3 L = normalize(mainLight.direction);

                float NdotL = saturate(dot(N, L));
                float NdotV = saturate(dot(N, V));

                float3 diffuse = _BaseColor.rgb * (0.25 + NdotL * 0.5);

                float rim = pow(1.0 - NdotV, _RimPower);
                float3 rimLight = _RimColor.rgb * rim * _RimIntensity;

                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), 96.0) * _Smoothness;
                float3 specular = spec * float3(1.0, 1.0, 1.0);

                float3 emission = _EmissionColor.rgb * _EmissionIntensity * 0.35;

                float3 color = diffuse + rimLight + specular + emission;

                return half4(color, _Alpha);
            }

            ENDHLSL
        }
    }
}