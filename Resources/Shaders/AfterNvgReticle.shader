Shader "Hidden/PiPDisabler/AfterNvgReticle"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _AfterNvgOn ("After NVG On", Float) = 0
        _AfterNvgColor ("After NVG Color", Color) = (0.86,0.95,0.82,1)
        _BlackPoint ("Black Point", Range(0,1)) = 0.04
        _WhitePoint ("White Point", Range(0,1)) = 0.22
        _ClipToVignette ("Clip To Vignette", Float) = 0
        _VignetteClipCenter ("Vignette Clip Center", Vector) = (0,0,0,0)
        _VignetteClipSize ("Vignette Clip Size", Vector) = (2,2,0,0)
        _VignetteClipRadius ("Vignette Clip Radius", Float) = 1
        _VignetteClipSoftness ("Vignette Clip Softness", Float) = 0

        _Stencil ("Stencil ID", Float) = 0
        _StencilComp ("Stencil Comparison", Float) = 8
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        _ZTest ("ZTest", Float) = 8
        _ZWrite ("ZWrite", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _AfterNvgOn;
            fixed4 _AfterNvgColor;
            float _BlackPoint;
            float _WhitePoint;
            float _ClipToVignette;
            float4 _VignetteClipCenter;
            float4 _VignetteClipSize;
            float _VignetteClipRadius;
            float _VignetteClipSoftness;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                float2 clipPos : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                o.clipPos = o.vertex.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, i.uv) * _Color * i.color;
                if (_ClipToVignette > 0.5)
                {
                    float2 halfSize = max(_VignetteClipSize.xy * 0.5, float2(0.0001, 0.0001));
                    float2 p = (i.clipPos - _VignetteClipCenter.xy) / halfSize;
                    float dist = length(p);
                    float radius = saturate(_VignetteClipRadius);
                    float outer = lerp(radius, 1.0, saturate(_VignetteClipSoftness));
                    float clipAlpha = 1.0 - smoothstep(radius, max(radius + 0.0001, outer), dist);
                    src.a *= clipAlpha;
                }

                if (_AfterNvgOn < 0.5)
                    return src;

                float brightness = max(src.r, max(src.g, src.b));
                float litMask = smoothstep(_BlackPoint, _WhitePoint, brightness);
                fixed3 rgb = lerp(fixed3(0, 0, 0), _AfterNvgColor.rgb, litMask);

                return fixed4(rgb, src.a * _AfterNvgColor.a);
            }
            ENDCG
        }
    }
}
